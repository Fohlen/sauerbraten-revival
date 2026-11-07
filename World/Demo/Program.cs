using SauerbratenRevival.World.Demo.Formats.Dmo;

namespace SauerbratenRevival;

/// <summary>
/// Entry point for standalone CLI execution (e.g. dotnet run -p:OutputType=Exe).
/// Delegates directly to <see cref="DmoConverter.Run"/>.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        return DmoConverter.Run(args);
    }
}
