using System;
using System.Windows;
using Vspd.Bus;

namespace VSPD
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 静默提权模式：DriverManager 会把本程序以 --vspd-install-driver / --vspd-stop-driver
            // 重新以管理员身份启动，由这里执行真正的内核操作后立即退出，不显示主窗口。
            var args = Environment.GetCommandLineArgs();
            bool install = false, stop = false;
            string? dir = null, result = null;
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--vspd-install-driver":
                        install = true;
                        if (i + 1 < args.Length) dir = args[++i];
                        break;
                    case "--vspd-stop-driver":
                        stop = true;
                        break;
                    case "--result":
                        if (i + 1 < args.Length) result = args[++i];
                        break;
                }
            }

            if (install || stop)
            {
                int code = stop ? DriverInstaller.Stop(result) : DriverInstaller.Run(dir, result);
                Shutdown(code);
                return; // 不调用 base.OnStartup，避免加载 StartupUri 主窗口
            }

            base.OnStartup(e);
        }
    }
}
