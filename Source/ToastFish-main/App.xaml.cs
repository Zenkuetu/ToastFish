using System;
using System.Windows;
using ToastFish.Model.SqliteControl;

namespace ToastFish
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 修复开机自启时工作目录不在 exe 目录的问题
            // Windows 注册表 Run 键启动时工作目录默认为 C:\Windows\System32，
            // 导致所有相对路径（Log、Resources、Mp3Cache）失效甚至权限异常崩溃
            string exeDir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
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

            // 学习前备份 dateLastReviewed → dateLastReviewed_bak
            try { Select.PreLearnBackup(); } catch { }
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
                    string exeDir = System.IO.Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
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
