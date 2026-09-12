using System.Security.Cryptography;
using Deep.Protocol.XPointNetworkV1;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Immutable production XPoint genesis anchor compiled into the client. Runtime
/// configuration may select transports, but it cannot replace this trust root.
/// </summary>
internal static class ActiveBuildXPointGenesisPin
{
    internal const string ProductionNetworkIdHex =
        "edc5dc1516a847a65fc8ba0e690d000d";
    internal const string ProductionGenesisHashHex =
        "304911104767ae1036a44c71116a5fcdee3449fc71ea1467c09295f89be3a2b7";

    internal static XPointNetworkGenesisPin? TryLoad()
    {
        var activeNetworkId = ActiveBuildNetworkId.Load().ToArray();
        var productionNetworkId = Convert.FromHexString(ProductionNetworkIdHex);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    activeNetworkId,
                    productionNetworkId))
            {
                return null;
            }

            return new XPointNetworkGenesisPin(
                activeNetworkId,
                Convert.FromHexString(ProductionGenesisHashHex));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(activeNetworkId);
            CryptographicOperations.ZeroMemory(productionNetworkId);
        }
    }
}
