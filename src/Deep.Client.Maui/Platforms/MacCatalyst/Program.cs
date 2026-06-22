#if MACCATALYST
using UIKit;

namespace Deep.Client.Maui;

public static class Program
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
#endif
