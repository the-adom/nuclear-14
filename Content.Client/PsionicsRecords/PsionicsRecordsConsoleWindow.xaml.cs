using Content.Client.UserInterface.Controls;
using Content.Shared.Access.Systems;
using Content.Shared.Administration;
using Content.Shared.PsionicsRecords;
using Content.Shared.Dataset;
using Content.Shared.Psionics;
using Content.Shared.StationRecords;
using Robust.Client.AutoGenerated;
using Robust.Client.Player;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

/// <summary>
/// EVERYTHING HERE IS A MODIFIED VERSION OF CRIMINAL RECORDS
/// </summary>

namespace Content.Client.PsionicsRecords;

// TODO: dedupe shitcode from general records theres a lot
[GenerateTypedNameReferences]
public sealed partial class PsionicsRecordsConsoleWindow : FancyWindow
{
    private readonly IPlayerManager _player;
    private readonly IPrototypeManager _proto;
    private readonly IRobustRandom _random;
    private readonly AccessReaderSystem _accessReader;

    public readonly EntityUid Console;

    [ValidatePrototypeId<DatasetPrototype>]
    private const string ReasonPlaceholders = "PsionicsRecordsRecordsPlaceholders";

    public Action<uint?>? OnKeySelected;
    public Action<StationRecordFilterType, string>? OnFiltersChanged;
    public Action<PsionicsStatus>? OnStatusSelected;
    public Action<PsionicsStatus, string>? OnDialogConfirmed;

    private uint _maxLength;
    private bool _isPopulating;
    private bool _access;
    private uint? _selectedKey;
    private PsionicsRecord? _selectedRecord;

    private DialogWindow? _reasonDialog;

    private StationRecordFilterType _currentFilterType;

    public PsionicsRecordsConsoleWindow(EntityUid console, uint maxLength, IPlayerManager playerManager, IPrototypeManager prototypeManager, IRobustRandom robustRandom, AccessReaderSystem accessReader)
    {
        RobustXamlLoader.Load(this);

        Console = console;
        _player = playerManager;
        _proto = prototypeManager;
        _random = robustRandom;
        _accessReader = accessReader;

        _maxLength = maxLength;
        _currentFilterType = StationRecordFilterType.Name;

        OpenCentered();

        foreach (var item in Enum.GetValues<StationRecordFilterType>())
        {
            FilterType.AddItem(GetTypeFilterLocals(item), (int) item);
        }

        foreach (var status in Enum.GetValues<PsionicsStatus>())
        {
            AddStatusSelect(status);
        }

        OnClose += () => _reasonDialog?.Close();

        RecordListing.OnItemSelected += args =>
        {
            if (_isPopulating || RecordListing[args.ItemIndex].Metadata is not uint cast)
                return;

            OnKeySelected?.Invoke(cast);
        };

        RecordListing.OnItemDeselected += _ =>
        {
            if (!_isPopulating)
                OnKeySelected?.Invoke(null);
        };

        FilterType.OnItemSelected += eventArgs =>
        {
            var type = (StationRecordFilterType) eventArgs.Id;

            if (_currentFilterType != type)
            {
                _currentFilterType = type;
                FilterListingOfRecords(FilterText.Text);
            }
        };

        FilterText.OnTextEntered += args =>
        {
            FilterListingOfRecords(args.Text);
        };

        StatusOptionButton.OnItemSelected += args =>
        {
            SetStatus((PsionicsStatus) args.Id);
        };
    }

    public void UpdateState(PsionicsRecordsConsoleState state)
    {
        if (state.Filter != null)
        {
            if (state.Filter.Type != _currentFilterType)
            {
                _currentFilterType = state.Filter.Type;
            }

            if (state.Filter.Value != FilterText.Text)
            {
                FilterText.Text = state.Filter.Value;
            }
        }

        _selectedKey = state.SelectedKey;

        FilterType.SelectId((int) _currentFilterType);

        // set up the records listing panel
        RecordListing.Clear();

        var hasRecords = state.RecordListing != null && state.RecordListing.Count > 0;
        NoRecords.Visible = !hasRecords;
        if (hasRecords)
            PopulateRecordListing(state.RecordListing!);

        // set up the selected person's record
        var selected = _selectedKey != null;

        PersonContainer.Visible = selected;
        RecordUnselected.Visible = !selected;

        _access = _player.LocalSession?.AttachedEntity is {} player
            && _accessReader.IsAllowed(player, Console);

        // hide access-required editing parts when no access
        var editing = _access && selected;
        StatusOptionButton.Disabled = !editing;

        if (state is { PsionicsRecord: not null, StationRecord: not null })
        {
            PopulateRecordContainer(state.StationRecord, state.PsionicsRecord);
            _selectedRecord = state.PsionicsRecord;
        }
        else
        {
            _selectedRecord = null;
        }
    }

    private void PopulateRecordListing(Dictionary<uint, string> listing)
    {
        _isPopulating = true;

        foreach (var (key, name) in listing)
        {
            var item = RecordListing.AddItem(name);
            item.Metadata = key;
            item.Selected = key == _selectedKey;
        }
        _isPopulating = false;

        RecordListing.SortItemsByText();
    }

    private void PopulateRecordContainer(GeneralStationRecord stationRecord, PsionicsRecord psionicsRecord)
    {
        var na = Loc.GetString("generic-not-available-shorthand");
        PersonName.Text = stationRecord.Name;

        StatusOptionButton.SelectId((int) psionicsRecord.Status);
        if (psionicsRecord.Reason is {} reason)
        {
            var message = FormattedMessage.FromMarkup(Loc.GetString("psionics-records-console-wanted-reason"));
            message.AddText($": {reason}");
            PsionicsList.SetMessage(message);
            PsionicsList.Visible = true;
        }
        else
        {
            PsionicsList.Visible = false;
        }
    }

    private void AddStatusSelect(PsionicsStatus status)
    {
        var name = Loc.GetString($"psionics-records-status-{status.ToString().ToLower()}");
        StatusOptionButton.AddItem(name, (int) status);
    }

    private void FilterListingOfRecords(string text = "")
    {
        if (!_isPopulating)
        {
            OnFiltersChanged?.Invoke(_currentFilterType, text);
        }
    }

    private void SetStatus(PsionicsStatus status)
    {
        if (status != PsionicsStatus.None) // All statuses should have a reasoning.
        {
            GetReason(status);
            return;
        }

        OnStatusSelected?.Invoke(status);
    }

    private void GetReason(PsionicsStatus status)
    {
        if (_reasonDialog != null)
        {
            _reasonDialog.MoveToFront();
            return;
        }

        var field = "reason";
        var title = Loc.GetString("psionics-records-status-" + status.ToString().ToLower());
        var placeholders = _proto.Index<DatasetPrototype>(ReasonPlaceholders);
        var placeholder = Loc.GetString("psionics-records-console-reason-placeholder", ("placeholder", _random.Pick(placeholders.Values))); // just funny it doesn't actually get used
        var prompt = Loc.GetString("psionics-records-console-reason");
        var entry = new QuickDialogEntry(field, QuickDialogEntryType.LongText, prompt, placeholder);
        var entries = new List<QuickDialogEntry>() { entry };
        _reasonDialog = new DialogWindow(title, entries);

        _reasonDialog.OnConfirmed += responses =>
        {
            var reason = responses[field];
            if (reason.Length < 1 || reason.Length > _maxLength)
                return;

            OnDialogConfirmed?.Invoke(status, reason);
        };

        _reasonDialog.OnClose += () => { _reasonDialog = null; };
    }

    private string GetTypeFilterLocals(StationRecordFilterType type)
    {
        return Loc.GetString($"psionics-records-{type.ToString().ToLower()}-filter");
    }
}
