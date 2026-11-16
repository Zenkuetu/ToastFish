using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using ToastFish.Model.SqliteControl;

namespace ToastFish
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        // #142：单实例互斥体。二次启动必须在「任何数据库动作之前」就退出 ——
        // 旧实现（MainWindow.Form_Load 扫进程名）已经太晚：那时 AutoSnapshotDb 正用
        // SQLite backup 复制 27MB 库、PreLearnBackup 还会改 dateLastReviewed_bak，
        // 磁盘抖动 + 抹掉正在跑那一轮的统计，这才是「再启动一次鼠标卡一下」的真因。
        private const string SingleInstanceMutexName = @"Local\ToastFish.SingleInstance";
        private static Mutex _instanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!TryAcquireSingleInstance())
            {
                System.Windows.Forms.MessageBox.Show(
                    "程序已经在运行了，不能运行两次。\n请在任务栏右下角的托盘图标上操作；如果确实已经退出，先在任务管理器里结束 ToastFish 任务。",
                    "提示", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
                // 直接退出，绝不进入 base.OnStartup：那里会按 StartupUri 建主窗口，
                // 进而注册热键、播静音预热、写数据库
                Environment.Exit(0);
                return;
            }

            // 修复开机自启时工作目录不在 exe 目录的问题
            // Windows 注册表 Run 键启动时工作目录默认为 C:\Windows\System32，
            // 导致所有相对路径（Log、Resources、Mp3Cache）失效甚至权限异常崩溃
            string exeDir = PathHelper.ExeDir;
            System.IO.Directory.SetCurrentDirectory(exeDir);

            base.OnStartup(e);

            // 全局后台线程异常兜底（配合 app.config legacyUnhandledExceptionPolicy）
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                System.Diagnostics.Debug.WriteLine("Unhandled exception: " + ex.ExceptionObject?.ToString());
            };

            // 自动数据库快照（2026-08-08 防污染机制）：启动时备份 inami.db 到 DbSnapshots，
            // 保留最近 5 份供回滚。必须在 AutoCheckAndFix 等写入前调用。
            try { Select.AutoSnapshotDb(); } catch { }

            // 自动修复 SM2+ 参数（首次或每 24 小时）
            try { Select.AutoCheckAndFix(); } catch { }

            // 建表（EssayLog 等新增表，幂等）
            try { Select.CreateEssayLogTable(); } catch { }

            // #133（3.4.10）：先把上次没来得及记账的成绩结算掉，再覆盖 dateLastReviewed_bak ——
            // 顺序反了的话，被强行结束（或断电）那一轮的成绩会被 PreLearnBackup() 抹掉
            try { Model.StudyLog.StudyLogManager.FlushPendingStudyLog(); } catch { }

            // 学习前备份 dateLastReviewed → dateLastReviewed_bak
            try { Select.PreLearnBackup(); } catch { }
        }

        /// <summary>
        /// #142：拿到单实例所有权返回 true；已有实例在跑返回 false。
        /// 互斥体是原子的，进程被强杀时操作系统会自动释放，不会留下「假占用」。
        /// </summary>
        private static bool TryAcquireSingleInstance()
        {
            try
            {
                bool createdNew;
                _instanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
                if (!createdNew) return false;
            }
            catch
            {
                // 互斥体创建失败（极端环境）不阻断启动，继续走进程名兜底
            }

            // 兜底：旧版本（没有互斥体）可能正在运行，按进程名再确认一次
            try
            {
                string selfName = Process.GetCurrentProcess().ProcessName;
                if (Process.GetProcessesByName(selfName).Length > 1) return false;
            }
            catch { }

            return true;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 兜底保护：正常退出由 ExitApp_Click 处理统计面板；
            // OnExit 仅作非常规退出（任务管理器结束等）的兜底
            try
            {
                var result = Select.PostLearnApply();
                if (result.TotalWords > 0)
                {
                    string exeDir = PathHelper.ExeDir;
                    string dbPath = System.IO.Path.Combine(exeDir, "Resources", "inami.db");
                    using (var db = new System.Data.SQLite.SQLiteConnection(
                        $"Data Source={dbPath};Version=3"))
                    {
                        db.Open();
                        Model.StudyLog.StudyLogManager.LogStudySession(db,
                            Select.TABLE_NAME, result.TotalWords,
                            result.EasyCount, result.GoodCount,
                            result.HardCount, result.AgainCount);
                    }
                }
            }
            catch { }

            base.OnExit(e);
        }
    }
}
