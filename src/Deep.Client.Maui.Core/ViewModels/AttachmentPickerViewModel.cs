using System.Collections.ObjectModel;
using Deep.Client.Maui.Core.Commands;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.ViewModels;

public interface IAttachmentPickerService
{
    Task<IReadOnlyList<AttachmentMetadata>> PickAsync(CancellationToken cancellationToken = default);
}

public sealed class AttachmentPickerViewModel : ViewModelBase
{
    private readonly IAttachmentPickerService picker;

    public AttachmentPickerViewModel(IAttachmentPickerService picker)
    {
        this.picker = picker;
        Attachments = [];
        PickCommand = new AsyncCommand(PickAsync);
    }

    public ObservableCollection<AttachmentMetadata> Attachments { get; }

    public AsyncCommand PickCommand { get; }

    public Task PickAsync(CancellationToken cancellationToken = default) =>
        RunBusyAsync(async ct =>
        {
            var picked = await picker.PickAsync(ct);
            foreach (var attachment in picked)
            {
                Attachments.Add(attachment);
            }
        }, cancellationToken);
}
