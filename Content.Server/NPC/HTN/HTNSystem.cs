using System.Linq;
using System.Text;
using System.Threading;
using Content.Server.Administration.Managers;
using Robust.Shared.CPUJob.JobQueues;
using Robust.Shared.CPUJob.JobQueues.Queues;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.Systems;
using Content.Shared.Administration;
using Content.Shared.Mobs;
using Content.Shared.NPC;
using JetBrains.Annotations;
using Robust.Server.GameObjects; // Corvax
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Content.Server.Worldgen; // Corvax
using Content.Server.Worldgen.Components; // Corvax
using Content.Server.Worldgen.Systems; // Corvax
using Robust.Server.GameObjects; // Corvax

namespace Content.Server.NPC.HTN;

public sealed class HTNSystem : EntitySystem
{
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly NPCUtilitySystem _utility = default!;
    // Corvax
    [Dependency] private readonly WorldControllerSystem _world = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    private EntityQuery<WorldControllerComponent> _mapQuery;
    private EntityQuery<LoadedChunkComponent> _loadedQuery;
    // Corvax

    private readonly JobQueue _planQueue = new(0.004);

    private readonly HashSet<ICommonSession> _subscribers = new();

    private const float ReplanRate = 4f; // per second, TODO: CVar?
    private float Accumulator; // limit replanning rate

    // Hierarchical Task Network
    public override void Initialize()
    {
        base.Initialize();
        _mapQuery = GetEntityQuery<WorldControllerComponent>(); // Corvax
        _loadedQuery = GetEntityQuery<LoadedChunkComponent>(); // Corvax
        SubscribeLocalEvent<HTNComponent, MobStateChangedEvent>(_npc.OnMobStateChange);
        SubscribeLocalEvent<HTNComponent, MapInitEvent>(_npc.OnNPCMapInit);
        SubscribeLocalEvent<HTNComponent, PlayerAttachedEvent>(_npc.OnPlayerNPCAttach);
        SubscribeLocalEvent<HTNComponent, PlayerDetachedEvent>(_npc.OnPlayerNPCDetach);
        SubscribeLocalEvent<HTNComponent, ComponentShutdown>(OnHTNShutdown);
        SubscribeNetworkEvent<RequestHTNMessage>(OnHTNMessage);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypeLoad);
        OnLoad();
    }

    private void OnHTNMessage(RequestHTNMessage msg, EntitySessionEventArgs args)
    {
        if (!_admin.HasAdminFlag(args.SenderSession, AdminFlags.Debug))
        {
            _subscribers.Remove(args.SenderSession);
            return;
        }

        if (_subscribers.Add(args.SenderSession))
            return;

        _subscribers.Remove(args.SenderSession);
    }

    private void OnLoad()
    {
        // Clear all NPCs in case they're hanging onto stale tasks
        var query = AllEntityQuery<HTNComponent>();

        while (query.MoveNext(out var comp))
        {
            comp.PlanningToken?.Cancel();
            comp.PlanningToken = null;

            if (comp.Plan != null)
            {
                var currentOperator = comp.Plan.CurrentOperator;
                ShutdownTask(currentOperator, comp.Blackboard, HTNOperatorStatus.Failed);
                ShutdownPlan(comp);
                comp.Plan = null;
                RequestPlan(comp);
            }
        }

        // Add dependencies for all operators.
        // We put code on operators as I couldn't think of a clean way to put it on systems.
        foreach (var compound in _prototypeManager.EnumeratePrototypes<HTNCompoundPrototype>())
        {
            UpdateCompound(compound);
        }
    }

    private void OnPrototypeLoad(PrototypesReloadedEventArgs obj)
    {
        OnLoad();
    }

    private void UpdateCompound(HTNCompoundPrototype compound)
    {
        for (var i = 0; i < compound.Branches.Count; i++)
        {
            var branch = compound.Branches[i];

            foreach (var precon in branch.Preconditions)
            {
                precon.Initialize(EntityManager.EntitySysManager);
            }

            foreach (var task in branch.Tasks)
            {
                UpdateTask(task);
            }
        }
    }

    private void UpdateTask(HTNTask task)
    {
        switch (task)
        {
            case HTNCompoundTask:
                // NOOP, handled elsewhere
                break;
            case HTNPrimitiveTask primitive:
                foreach (var precon in primitive.Preconditions)
                {
                    precon.Initialize(EntityManager.EntitySysManager);
                }

                primitive.Operator.Initialize(EntityManager.EntitySysManager);
                break;
            default:
                throw new NotImplementedException();
        }
    }

    private void OnHTNShutdown(EntityUid uid, HTNComponent component, ComponentShutdown args)
    {
        _npc.OnNPCShutdown(uid, component, args);
        component.PlanningToken?.Cancel();
        component.PlanningJob = null;
    }

    /// <summary>
    /// Forces the NPC to replan.
    /// </summary>
    [PublicAPI]
    public void Replan(HTNComponent component)
    {
        component.NextPlanTime = _gameTiming.CurTime;
    }

    public void UpdateNPC(ref int count, int maxUpdates, float frameTime)
    {
        _planQueue.Process();

        // Limit update rate
        const float updatePeriod = 1/ReplanRate;
        Accumulator += frameTime;
        if (Accumulator < updatePeriod)
            return;
        Accumulator -= updatePeriod;

        var query = EntityQueryEnumerator<ActiveNPCComponent, HTNComponent>();

        while(query.MoveNext(out var uid, out _, out var comp))
        {
            // If we're over our max count or it's not MapInit then ignore the NPC.
            if (count >= maxUpdates)
                break;

            if (!IsNPCActive(uid))  // Corvax
                continue;

            if (comp.PlanningJob != null)
            {
                if (comp.PlanningJob.Exception != null)
                {
                    Log.Fatal($"Received exception on planning job for {uid}!");
                    _npc.SleepNPC(uid);
                    var exc = comp.PlanningJob.Exception;
                    RemComp<HTNComponent>(uid);
                    throw exc;
                }

                // If a new planning job has finished then handle it.
                if (comp.PlanningJob.Status != JobStatus.Finished)
                    continue;

                var newPlanBetter = false;

                // If old traversal is better than new traversal then ignore the new plan
                if (comp.Plan != null && comp.PlanningJob.Result != null)
                {
                    var oldMtr = comp.Plan.BranchTraversalRecord;
                    var mtr = comp.PlanningJob.Result.BranchTraversalRecord;

                    for (var i = 0; i < oldMtr.Count; i++)
                    {
                        if (i < mtr.Count && oldMtr[i] > mtr[i])
                        {
                            newPlanBetter = true;
                            break;
                        }
                    }
                }

                if (comp.Plan == null || newPlanBetter)
                {
                    comp.CheckServices = false;

                    if (comp.Plan != null)
                    {
                        ShutdownTask(comp.Plan.CurrentOperator, comp.Blackboard, HTNOperatorStatus.BetterPlan);
                        ShutdownPlan(comp);
                    }

                    comp.Plan = comp.PlanningJob.Result;

                    // Startup the first task and anything else we need to do.
                    if (comp.Plan != null)
                    {
                        StartupTask(comp.Plan.Tasks[comp.Plan.Index], comp.Blackboard, comp.Plan.Effects[comp.Plan.Index]);
                    }
                }
                // Keeping old plan
                else
                {
                    comp.CheckServices = true;
                }

                comp.PlanningJob = null;
                comp.PlanningToken = null;
            }

            Update(comp, frameTime);
            count++;
        }
    }
// Corvax-start
    private bool IsNPCActive(EntityUid entity)
    {
        var transform = Transform(entity);

        if (!_mapQuery.TryGetComponent(transform.MapUid, out var worldComponent))
            return true;

        var chunk = _world.GetOrCreateChunk(WorldGen.WorldToChunkCoords(_transform.GetWorldPosition(transform)).Floored(), transform.MapUid.Value, worldComponent);

        return _loadedQuery.TryGetComponent(chunk, out var loaded) && loaded.Loaders is not null;
    }
// Corvax-end

    private void HTNDebug(HTNComponent comp)
    {
        // Send debug info
        foreach (var session in _subscribers)
        {
            var text = new StringBuilder();

            if (comp.Plan != null)
            {
                text.AppendLine($"BTR: {string.Join(", ", comp.Plan.BranchTraversalRecord)}");
                text.AppendLine($"tasks:");
                var root = comp.RootTask;
                var btr = new List<int>();
                var level = -1;
                AppendDebugText(root, text, comp.Plan.BranchTraversalRecord, btr, ref level, comp.Plan);
            }

            RaiseNetworkEvent(new HTNMessage()
            {
                Uid = GetNetEntity(comp.Owner),
                Text = text.ToString(),
            }, session.Channel);
        }
    }

    private void AppendDebugText(HTNTask task, StringBuilder text, List<int> planBtr, List<int> btr, ref int level, HTNPlan plan)
    {
        // If it's the selected BTR then highlight.
        for (var i = 0; i < btr.Count; i++)
        {
            text.Append("--");
        }

        text.Append(' ');

        if (task is HTNPrimitiveTask primitive)
        {
            // Highlight current task
            if (plan.CurrentTask == primitive && btr.SequenceEqual(plan.BranchTraversalRecord))
            {
                // Still results in false positive if current branch contains multiple of the same task...
                text.Append("> ");
            }
            text.AppendLine(primitive.ToString());
            return;
        }

        if (task is HTNCompoundTask compTask)
        {
            var compound = _prototypeManager.Index<HTNCompoundPrototype>(compTask.Task);
            level++;
            text.AppendLine(compound.ID);
            var branches = compound.Branches;

            for (var i = 0; i < branches.Count; i++)
            {
                var branch = branches[i];
                btr.Add(i);

                foreach (var sub in branch.Tasks)
                {
                    AppendDebugText(sub, text, planBtr, btr, ref level, plan);
                }

                btr.RemoveAt(btr.Count - 1);
            }

            level--;
            return;
        }

        throw new NotImplementedException();
    }

    private void Update(HTNComponent component, float frameTime)
    {
        // We'll still try re-planning occasionally even when we're updating in case new data comes in.
        if (component.NextPlanTime <= _gameTiming.CurTime)
        {
            RequestPlan(component);
        }

        // Getting a new plan so do nothing.
        if (component.Plan == null)
            return;

        // Run the existing plan still
        var status = HTNOperatorStatus.Finished;

        // Continuously run operators until we can't anymore.
        while (status != HTNOperatorStatus.Continuing && component.Plan != null)
        {
            // Run the existing operator
            var currentOperator = component.Plan.CurrentOperator;
            var currentTask = component.Plan.CurrentTask;
            var blackboard = component.Blackboard;

            // Service still on cooldown.
            if (component.CheckServices)
            {
                foreach (var service in currentTask.Services)
                {
                    var serviceResult = _utility.GetEntities(blackboard, service.Prototype);
                    blackboard.SetValue(service.Key, serviceResult.GetHighest());
                }

                component.CheckServices = false;
            }

            status = currentOperator.Update(blackboard, frameTime);

            switch (status)
            {
                case HTNOperatorStatus.Continuing:
                    break;
                case HTNOperatorStatus.Failed:
                    ShutdownTask(currentOperator, blackboard, status);
                    ShutdownPlan(component);
                    break;
                // Operator completed so go to the next one.
                case HTNOperatorStatus.Finished:
                    ShutdownTask(currentOperator, blackboard, status);
                    component.Plan.Index++;

                    // Plan finished!
                    if (component.Plan.Tasks.Count <= component.Plan.Index)
                    {
                        ShutdownPlan(component);
                        break;
                    }

                    ConditionalShutdown(component.Plan, currentOperator, blackboard, HTNPlanState.TaskFinished);
                    StartupTask(component.Plan.Tasks[component.Plan.Index], component.Blackboard, component.Plan.Effects[component.Plan.Index]);
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }

        HTNDebug(component);
    }

    public void ShutdownTask(HTNOperator currentOperator, NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        if (currentOperator is IHtnConditionalShutdown conditional &&
            (conditional.ShutdownState & HTNPlanState.TaskFinished) != 0x0)
        {
            conditional.ConditionalShutdown(blackboard);
        }

        currentOperator.TaskShutdown(blackboard, status);
    }

    public void ShutdownPlan(HTNComponent component)
    {
        DebugTools.Assert(component.Plan != null);
        var blackboard = component.Blackboard;

        foreach (var task in component.Plan.Tasks)
        {
            if (task.Operator is IHtnConditionalShutdown conditional &&
                (conditional.ShutdownState & HTNPlanState.PlanFinished) != 0x0)
            {
                conditional.ConditionalShutdown(blackboard);
            }

            task.Operator.PlanShutdown(component.Blackboard);
        }

        component.Plan = null;
    }

    /// <summary>
    /// Shuts down the current operator conditionally.
    /// </summary>
    private void ConditionalShutdown(HTNPlan plan, HTNOperator currentOperator, NPCBlackboard blackboard, HTNPlanState state)
    {
        if (currentOperator is not IHtnConditionalShutdown conditional)
            return;

        if ((conditional.ShutdownState & state) == 0x0)
            return;

        conditional.ConditionalShutdown(blackboard);
    }

    /// <summary>
    /// Starts a new primitive task. Will apply effects from planning if applicable.
    /// </summary>
    private void StartupTask(HTNPrimitiveTask primitive, NPCBlackboard blackboard, Dictionary<string, object>? effects)
    {
        // We may have planner only tasks where we want to reuse their data during update
        // e.g. if we pathfind to an enemy to know if we can attack it, we don't want to do another pathfind immediately
        if (effects != null && primitive.ApplyEffectsOnStartup)
        {
            foreach (var (key, value) in effects)
            {
                blackboard.SetValue(key, value);
            }
        }

        primitive.Operator.Startup(blackboard);
    }

    /// <summary>
    /// Request a new plan for this component, even if running an existing plan.
    /// </summary>
    /// <param name="component"></param>
    private void RequestPlan(HTNComponent component)
    {
        if (component.PlanningJob != null)
            return;

        component.NextPlanTime = _gameTiming.CurTime + TimeSpan.FromSeconds(component.PlanCooldown);
        var cancelToken = new CancellationTokenSource();
        var branchTraversal = component.Plan?.BranchTraversalRecord;

        var job = new HTNPlanJob(
            0.02,
            _prototypeManager,
            component.RootTask,
            component.Blackboard.ShallowClone(), branchTraversal, cancelToken.Token);

        _planQueue.EnqueueJob(job);
        component.PlanningJob = job;
        component.PlanningToken = cancelToken;
    }

    public string GetDomain(HTNCompoundTask compound)
    {
        // TODO: Recursively add each one
        var indent = 0;
        var builder = new StringBuilder();
        AppendDomain(builder, compound, ref indent);

        return builder.ToString();
    }

    private void AppendDomain(StringBuilder builder, HTNTask task, ref int indent)
    {
        var buffer = string.Concat(Enumerable.Repeat("    ", indent));

        if (task is HTNPrimitiveTask primitive)
        {
            builder.AppendLine(buffer + $"Primitive: {task}");
            builder.AppendLine(buffer + $"  operator: {primitive.Operator.GetType().Name}");
        }
        else if (task is HTNCompoundTask compTask)
        {
            var compound = _prototypeManager.Index<HTNCompoundPrototype>(compTask.Task);
            builder.AppendLine(buffer + $"Compound: {task}");

            for (var i = 0; i < compound.Branches.Count; i++)
            {
                var branch = compound.Branches[i];

                builder.AppendLine(buffer + "  branch:");
                indent++;

                foreach (var branchTask in branch.Tasks)
                {
                    AppendDomain(builder, branchTask, ref indent);
                }

                indent--;
            }
        }
    }
}

/// <summary>
/// The outcome of the current operator during update.
/// </summary>
public enum HTNOperatorStatus : byte
{
    Continuing,
    Failed,
    Finished,

    /// <summary>
    /// Was a better plan than this found?
    /// </summary>
    BetterPlan,
}
