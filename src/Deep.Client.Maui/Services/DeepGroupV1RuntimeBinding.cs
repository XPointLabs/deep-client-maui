using Deep.Client.Shared.Persistence.GroupV1;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Persistence.MessagingV1;
using Deep.Client.Shared.Services.GroupV1;
using Deep.Client.Shared.Domain.MessagingV1;

namespace Deep.Client.Maui.Services;

internal sealed record DeepGroupV1RuntimeBinding(
    SqliteGroupStateStore StateStore,
    SqliteGroupInvitationActivationStore InvitationActivationStore,
    MessageStoreScope MessagingScope,
    IDeepGroupV1Runtime Runtime);
