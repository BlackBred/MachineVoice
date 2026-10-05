using MachineVoice.Protocol;

namespace MachineVoice.App;

public partial class GeneralPage : SettingsPage
{
    readonly Shown<Values> _shown = new(new Values(PlaybackMode.Auto, QueueOrder.Lifo, HeadingMode.Always));

    public GeneralPage()
    {
        InitializeComponent();
        Watch(ModeAuto, ModeConfirm, ModeSilent, OrderLifo, OrderFifo, HeadingAlways, HeadingOnChange, HeadingNever);
        UseSaveBar(SaveButton, SaveStatus);
        Show(_shown.Value);
    }

    public override string Title => "Общее";

    public override bool Edited => Read() != _shown.Value;

    public override void Update(ControlState state)
    {
        var saved = new Values(state.Mode, state.Settings.Order, state.Settings.Heading);
        if (!_shown.Accept(saved))
            return;
        var (read, shown) = (Read(), _shown.Value);
        _shown.Set(saved);
        Show(new Values(
            read.Mode == shown.Mode ? saved.Mode : read.Mode,
            read.Order == shown.Order ? saved.Order : read.Order,
            read.Heading == shown.Heading ? saved.Heading : read.Heading));
    }

    protected override async Task<string?> SaveCoreAsync()
    {
        var values = Read();
        var result = await Client.UpdateSettingsAsync(mode: values.Mode, order: values.Order, heading: values.Heading);
        if (!result.Ok)
            return Rejected(result);
        _shown.Saved(values);
        return null;
    }

    void Show(Values values) => Showing(() =>
    {
        ModeAuto.IsChecked = values.Mode == PlaybackMode.Auto;
        ModeConfirm.IsChecked = values.Mode == PlaybackMode.Confirm;
        ModeSilent.IsChecked = values.Mode == PlaybackMode.Silent;
        OrderLifo.IsChecked = values.Order == QueueOrder.Lifo;
        OrderFifo.IsChecked = values.Order == QueueOrder.Fifo;
        HeadingAlways.IsChecked = values.Heading == HeadingMode.Always;
        HeadingOnChange.IsChecked = values.Heading == HeadingMode.OnChange;
        HeadingNever.IsChecked = values.Heading == HeadingMode.Never;
    });

    Values Read() => new(
        ModeConfirm.IsChecked == true ? PlaybackMode.Confirm : ModeSilent.IsChecked == true ? PlaybackMode.Silent : PlaybackMode.Auto,
        OrderFifo.IsChecked == true ? QueueOrder.Fifo : QueueOrder.Lifo,
        HeadingNever.IsChecked == true ? HeadingMode.Never : HeadingOnChange.IsChecked == true ? HeadingMode.OnChange : HeadingMode.Always);

    sealed record Values(PlaybackMode Mode, QueueOrder Order, HeadingMode Heading);
}
