using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Independent process exercising the real MPC-HC WM_COPYDATA protocol, without user media.
class IpcPlayerFixture : NativeWindow
{
    [StructLayout(LayoutKind.Sequential)] struct CopyData { public IntPtr Command; public int Bytes; public IntPtr Data; }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr sender, ref CopyData data);
    readonly IntPtr owner;
    readonly string url;
    int requests;
    IpcPlayerFixture(IntPtr owner, string url)
    {
        this.owner = owner; this.url = url;
        CreateHandle(new CreateParams { Caption = "MPC API test fixture", Parent = new IntPtr(-3) });
    }
    void Send(uint command, string value)
    {
        var buffer = Marshal.StringToHGlobalUni(value);
        try { var data = new CopyData { Command = new IntPtr((long)command), Bytes = (value.Length + 1) * 2, Data = buffer }; SendMessage(owner, 0x4A, Handle, ref data); }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x4A)
        {
            var data = (CopyData)Marshal.PtrToStructure(message.LParam, typeof(CopyData));
            var command = data.Command.ToInt64();
            if (command == 0xA0003002) Send(0x50000003, "Fixture|Author|Description|" + url + "|100");
            if (command == 0xA0003004) { requests++; Send(0x50000007, requests == 1 ? "89" : "90"); }
            message.Result = new IntPtr(1); return;
        }
        base.WndProc(ref message);
    }
    [STAThread] static void Main(string[] args)
    {
        var index = Array.IndexOf(args, "/slave");
        var fixture = new IpcPlayerFixture(new IntPtr(Int64.Parse(args[index + 1])), args[0]);
        var timer = new Timer { Interval = 100 };
        var started = DateTime.UtcNow; bool connected = false;
        timer.Tick += delegate {
            if (!connected) { connected = true; fixture.Send(0x50000000, fixture.Handle.ToInt64().ToString()); }
            if (fixture.requests >= 2 || DateTime.UtcNow - started > TimeSpan.FromSeconds(10)) { timer.Stop(); fixture.DestroyHandle(); Application.ExitThread(); }
        };
        timer.Start(); Application.Run(); timer.Dispose();
    }
}
