using System.Windows;
using Forms = System.Windows.Forms;

namespace SshBroker.Gui;

public sealed class TrayHost : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _image;
    private readonly Forms.ContextMenuStrip _menu;
    public TrayHost(Action show, Action help, Action exit)
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SshBroker.ico"));
        using var stream = resource.Stream;
        using var loaded = new System.Drawing.Icon(stream);
        _image = (System.Drawing.Icon)loaded.Clone();
        _menu = new();
        _menu.Items.Add("显示窗口", null, (_, _) => show());
        _menu.Items.Add("使用帮助", null, (_, _) => help());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出程序", null, (_, _) => exit());
        _icon = new() { Icon = _image, Text = "SSH Broker", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); _image.Dispose(); }
}
