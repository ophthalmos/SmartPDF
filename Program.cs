using SmartPDF.Forms;

namespace SmartPDF;

internal static class Program
{
    /// <summary>Aufruf: SmartPDF [datei.pdf]</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}
