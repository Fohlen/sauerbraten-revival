using SauerbratenRevival.World.Demo.Formats.Dmo;

namespace SauerbratenRevival.Tools.DmoReader;

public static class Program
{
    public static int Main(string[] args)
    {
        return DmoConverter.Run(args);
    }
}
