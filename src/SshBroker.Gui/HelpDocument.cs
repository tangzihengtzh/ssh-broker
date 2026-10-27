using System.Diagnostics;
using System.IO;

namespace SshBroker.Gui;

public static class HelpDocument
{
    public static void Open()
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, "docs", "USER-GUIDE.html");
        if (!File.Exists(packaged)) throw new IOException("帮助文档缺失，请保留完整程序目录。");
        Process.Start(new ProcessStartInfo(packaged) { UseShellExecute = true });
    }
}
