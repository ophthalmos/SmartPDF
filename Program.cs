using MoziPDF.Forms;

namespace MoziPDF;

internal static class Program
{
    /// <summary>Aufruf: MoziPDF [datei.pdf]</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}
