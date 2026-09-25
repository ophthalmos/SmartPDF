using MozillaPDF.Forms;

namespace MozillaPDF;

internal static class Program
{
    /// <summary>Aufruf: MozillaPDF [datei.pdf]</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}
