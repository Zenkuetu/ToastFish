using Microsoft.Toolkit.Uwp.Notifications;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using ToastFish.ViewModel;
using ToastFish.Resources;
using System.Windows.Forms;
using ToastFish.Model.SqliteControl;
using System.Threading;
using ToastFish.Model.Mp3;
using System.Diagnostics;
using ToastFish.Model.PushControl;
using ToastFish.Model.Log;
using System.Speech.Synthesis;
using ToastFish.Model.StartWithWindows;
using ToastFish.View;
using System.IO;
using System.Windows.Xps.Packaging;
using System.Windows.Input;

namespace ToastFish
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        
        ToastFishModel Vm = new ToastFishModel();
        Select Se = new Select();
        PushWords pushWords = new PushWords();
        Thread thread = new Thread(new ParameterizedThreadStart(PushWords.Recitation));
        Dictionary<string, string> TablelDictionary = new Dictionary<string, string>(){
        {"CET4_1", "四级核心词汇"},{"CET4_3", "四级完整词汇"},{"CET6_1", "六级核心词汇"},
        {"CET6_3", "六级完整词汇"},{"GMAT_3", "GMAT词汇"},{"GRE_2", "GRE词汇"},
        {"IELTS_3", "IELTS词汇"},{"TOEFL_2", "TOEFL词汇"},{"SAT_2", "SAT词汇"},
        {"KaoYan_1", "考研必考词汇"},{"KaoYan_2", "考研完整词汇"},{"KaoYan_3", "考研词频词汇"},{"Level4_1", "专四真题高频词"},
        {"Level4luan_2", "专四核心词汇"},{"Level8_1", "专八真题高频词"},{"Level8luan_2", "专八核心词汇"},
        {"Goin", "顺序五十音"},{"StdJp_Mid", "标准日本语中级词汇"} };
       // private NotifyIcon _notifyIcon = null;
       //HotKey _hotKey0, _hotKey1, _hotKey2, _hotKey3, _hotKey4;
        public MainWindow()
        {
            Form_Load();
            InitializeComponent();
            PushWords.UIDispatcher = this.Dispatcher;
            DataContext = Vm;
            SetNotifyIcon();
            this.Visibility = Visibility.Hidden;
            Se.LoadGlobalConfig();
            ContextMenu();
            new HotKey(Key.Oem3, KeyModifier.Alt , OnHotKeyHandler);
            new HotKey(Key.D1, KeyModifier.Alt , OnHotKeyHandler);
            new HotKey(Key.D2, KeyModifier.Alt , OnHotKeyHandler);
            new HotKey(Key.D3, KeyModifier.Alt , OnHotKeyHandler);
            new HotKey(Key.D4, KeyModifier.Alt , OnHotKeyHandler);
            new HotKey(Key.Q, KeyModifier.Alt, OnHotKeyHandler);
            new HotKey(Key.H, KeyModifier.Alt, OnHotKeyHandler);  // ALT+H 隐藏/显示学习弹窗

            // 谜之bug，如果不先播放一段音频，那么什么声音都播不出来。
            // 所以播个没声音的音频先。
            PlayMute();

            // 启动惊喜复习后台监控
            SurpriseReviewManager.Start();

            // 启动后延迟静默检查 GitHub 更新：有新版才弹窗，其余情况完全静默（2026-10-15 新增）
            StartUpdateCheck(false);
            //this.WindowState = (WindowState)FormWindowState.Minimized;
        }


        private void OnHotKeyHandler(HotKey hotKey)
        {
            string key = hotKey.Key.ToString();
            Debug.WriteLine("key pressed:" + key);
            switch (key) 
            {
                case "Q":
                    Begin_Click(null, null);
                    break;
                case "D1":
                    PushWords.HotKeytObservable.raiseEvent("1");
                    break;
                case "D2":
                    PushWords.HotKeytObservable.raiseEvent("2");
                    break;
                case "D3":
                    PushWords.HotKeytObservable.raiseEvent("3");
                    break;
                case "D4":
                    PushWords.HotKeytObservable.raiseEvent("4");
                    break;
                case "Oem3":
                    PushWords.HotKeytObservable.raiseEvent("S");
                    break;
                case "H":
                    // #94（3.4.1）自愈：弹窗已经没了但这一轮还挂着，按 Alt+H 直接重开，
                    // 而不是像旧版那样什么都不做（只能重启软件）。
                    if (!NotificationForm.HasAnyLivePopup && PushWords.IsLearningActive)
                        Begin_Click(null, EventArgs.Empty);
                    else
                        NotificationForm.ToggleVisibility();
                    break;
                default:
                    break;
            }           
        }

        private void Form_Load()

        {
            //获取当前活动进程的模块名称
            string moduleName = Process.GetCurrentProcess().MainModule.ModuleName;
            //返回指定路径字符串的文件名
            string processName = System.IO.Path.GetFileNameWithoutExtension(moduleName);
            //根据文件名创建进程资源数组
            Process[] processes = Process.GetProcessesByName(processName);
            //如果该数组长度大于1，说明多次运行
            if (processes.Length > 1)
            {
                System.Windows.Forms.MessageBox.Show("程序已经在运行了，不能运行两次。\n如果右下角软件已经退出，请在任务管理器中结束ToastFish任务。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);//弹出提示信息
                this.Close();//关闭当前窗体
            }
        }


        private void SetNotifyIcon()
        {
            Vm.notifyIcon = new NotifyIcon();
            Vm.notifyIcon.Text = "ToastFish";
            System.Drawing.Icon icon = IconChika.chika16;

            Vm.notifyIcon.Icon = icon;
            Vm.notifyIcon.Visible = true;
            Vm.notifyIcon.DoubleClick += Begin_Click;
            //Vm.notifyIcon.DoubleClick += NotifyIconDoubleClick;
        }

        /// <summary>
        /// 预热音频设备（不播这一段，后面什么声音都播不出来）。
        /// #105：绝不能放在 UI 线程上 —— 某些声卡/驱动下 mciSendString("play media wait") 会永久阻塞，
        /// 把 UI 线程整个卡死（表现为托盘图标左右键都没反应、ALT+Q 热键失效、只能重启程序）。
        /// 这里丢到后台线程，卡住也只卡这一条线程，主界面/托盘/热键不受影响。
        /// </summary>
        public void PlayMute()
        {
            System.Threading.Thread th = new System.Threading.Thread(() =>
            {
                try
                {
                    MUSIC Temp = new MUSIC();
                    Temp.FileName = PathHelper.ExeDir + @"\Resources\mute.mp3";
                    Temp.play();
                }
                catch
                {
                    // 预热失败不影响任何功能，静默忽略
                }
            });
            th.IsBackground = true;
            th.Priority = System.Threading.ThreadPriority.BelowNormal;
            th.Start();
        }

        private void NotifyIconDoubleClick(object sender, EventArgs e)
        {
            this.Activate();
            this.WindowState = WindowState.Normal;
            this.ShowInTaskbar = true;
            this.Topmost = true;
            this.Show();
        }

        #region 托盘右键菜单

        System.Windows.Forms.ToolStripMenuItem Begin = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem Settings = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SetNumber = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SetEngType = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem ImportWords = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SelectBook = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SelectJpBook = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem RandomTest = new System.Windows.Forms.ToolStripMenuItem();

        System.Windows.Forms.ToolStripMenuItem GotoHtml = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem Dashboard = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem Start = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem ExitMenuItem = new System.Windows.Forms.ToolStripMenuItem();

        System.Windows.Forms.ToolStripMenuItem SetAutoPlay = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SetAutoLog = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SetAiEssay = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SetAiModel = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SetAiKey = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SetAiProvider = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SetAiModels = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SetAiFastModel = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SetAiReasonModel = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem SetTestMode = new System.Windows.Forms.ToolStripMenuItem();
        System.Windows.Forms.ToolStripMenuItem CheckUpdate = new System.Windows.Forms.ToolStripMenuItem();

        // 更新检查：启动后延迟多久开始静默检查 GitHub（毫秒）
        private const int UPDATE_CHECK_DELAY_MS = 15000;
        private new void ContextMenu()
        {
            ContextMenuStrip Cms = new ContextMenuStrip();

            Vm.notifyIcon.ContextMenuStrip = Cms;


            Begin.Text = "开始！";
            Begin.Click += new EventHandler(Begin_Click);
            Settings.Text = "参数设置";


            SetNumber.Text = "单词个数";
            SetNumber.Click += new EventHandler(SetNumber_Click);

            SetEngType.Text = "英标类型";
            SetEngType.Click += new EventHandler(SetEngType_Click);

            SetAutoPlay.Text="自动播放";
            SetAutoPlay.Click += new EventHandler(AutoPlay_Click);
            if (Select.AUTO_PLAY != 0)
                SetAutoPlay.Checked = true;
            else
                SetAutoPlay.Checked = false;

            SetAutoLog.Text = "自动日志";
            SetAutoLog.Click += new EventHandler(AutoLog_Click);
            if (Select.AUTO_LOG != 0)
                SetAutoLog.Checked = true;
            else
                SetAutoLog.Checked = false;

            // 学后阅读模式切换：勾选 = AI 短文，不勾选 = 例句串读（默认）
            Model.Ai.AiConfig.Load();
            SetAiEssay.Text = "学后AI短文";
            SetAiEssay.Click += new EventHandler(AiEssay_Click);
            SetAiEssay.Checked = (Model.Ai.AiConfig.ReadingMode == 1);

            // AI 模型切换：勾选 = 推理模型（pro 深度思考，不限字数），不勾选 = 快速模型（flash）
            SetAiModel.Text = "AI短文用推理模型";
            SetAiModel.Click += new EventHandler(AiModel_Click);
            SetAiModel.Checked = (Model.Ai.AiConfig.ModelMode == 1);

            // AI 模型名设置：任何服务商都能改快速/推理模型名，按服务商分别记忆（以后出模型不用改代码）
            SetAiModels.Text = "设置 AI 模型名";
            SetAiFastModel = MakeModelItem("快速模型名（" + Model.Ai.AiConfig.Model + "）", false);
            SetAiReasonModel = MakeModelItem("推理模型名（" + Model.Ai.AiConfig.ReasonModel + "）", true);
            SetAiModels.DropDownItems.Add(SetAiFastModel);
            SetAiModels.DropDownItems.Add(SetAiReasonModel);

            // AI 服务商切换：DeepSeek / Kimi（开放平台）/ Kimi Code 订阅 / 自定义（任意 OpenAI 兼容端点）
            SetAiProvider.Text = "AI 服务商（" + Model.Ai.AiConfig.ProviderName + "）";
            SetAiProvider.DropDownItems.Add(MakeProviderItem("DeepSeek", Model.Ai.AiConfig.ProviderDeepSeek));
            SetAiProvider.DropDownItems.Add(MakeProviderItem("Kimi（开放平台）", Model.Ai.AiConfig.ProviderKimi));
            SetAiProvider.DropDownItems.Add(MakeProviderItem("Kimi Code 订阅", Model.Ai.AiConfig.ProviderKimiCode));
            SetAiProvider.DropDownItems.Add(MakeProviderItem("自定义（兼容 OpenAI）", Model.Ai.AiConfig.ProviderCustom));

            SetAiKey.Text = "设置 " + Model.Ai.AiConfig.ProviderName + " Key";
            SetAiKey.Click += new EventHandler(AiKey_Click);

            SetTestMode.Text = "测试模式";
            SetTestMode.Click += new EventHandler(TestMode_Click);
            SetTestMode.Checked = PushWords.TestMode;

            ImportWords.Text = "导入单词";
            ImportWords.Click += new EventHandler(ImportWords_Click);

            SelectBook.Text = "英语词汇";

            SelectJpBook.Text = "日语词汇";

            RandomTest.Text = "随机测试";

            CheckUpdate.Text = "检查更新";
            CheckUpdate.Click += new EventHandler(CheckUpdate_Click);

            GotoHtml.Text = "使用说明";
            GotoHtml.Click += new EventHandler(HowToUse_Click);

            Dashboard.Text = "学习报告 \U0001f4ca";
            Dashboard.Click += new EventHandler(Dashboard_Click);

            Start.Text = "开机启动";
            Start.Click += new EventHandler(Start_Click);
            Start.Checked = AutoStartHelper.IsEnabled();

            // 首次迁移：旧版用启动文件夹快捷方式，自动迁移到注册表
            string oldStartupLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ToastFish.lnk");
            if (!Start.Checked && File.Exists(oldStartupLnk))
            {
                AutoStartHelper.Enable();
                try { File.Delete(oldStartupLnk); } catch { }
                Start.Checked = true;
            }

            ExitMenuItem.Text = "退出";
            ExitMenuItem.Click += new EventHandler(ExitApp_Click);

            ToolStripItem CET4_1 = new ToolStripMenuItem("四级核心词汇");
            CET4_1.Click += new EventHandler(SelectBook_Click);
            ToolStripItem CET4_3 = new ToolStripMenuItem("四级完整词汇");
            CET4_3.Click += new EventHandler(SelectBook_Click);
            ToolStripItem CET6_1 = new ToolStripMenuItem("六级核心词汇");
            CET6_1.Click += new EventHandler(SelectBook_Click);
            ToolStripItem CET6_3 = new ToolStripMenuItem("六级完整词汇");
            CET6_3.Click += new EventHandler(SelectBook_Click);
            ToolStripItem GMAT_3 = new ToolStripMenuItem("GMAT词汇");
            GMAT_3.Click += new EventHandler(SelectBook_Click);
            ToolStripItem GRE_2 = new ToolStripMenuItem("GRE词汇");
            GRE_2.Click += new EventHandler(SelectBook_Click);
            ToolStripItem IELTS_3 = new ToolStripMenuItem("IELTS词汇");
            IELTS_3.Click += new EventHandler(SelectBook_Click);
            ToolStripItem TOEFL_2 = new ToolStripMenuItem("TOEFL词汇");
            TOEFL_2.Click += new EventHandler(SelectBook_Click);
            ToolStripItem SAT_2 = new ToolStripMenuItem("SAT词汇");
            SAT_2.Click += new EventHandler(SelectBook_Click);
            ToolStripItem KaoYan_1 = new ToolStripMenuItem("考研必考词汇");
            KaoYan_1.Click += new EventHandler(SelectBook_Click);
            ToolStripItem KaoYan_2 = new ToolStripMenuItem("考研完整词汇");
            KaoYan_2.Click += new EventHandler(SelectBook_Click);
            ToolStripItem KaoYan_3 = new ToolStripMenuItem("考研词频词汇");
            KaoYan_3.Click += new EventHandler(SelectBook_Click);
            ToolStripItem Level4_1 = new ToolStripMenuItem("专四真题高频词");
            Level4_1.Click += new EventHandler(SelectBook_Click);
            ToolStripItem Level4luan_2 = new ToolStripMenuItem("专四核心词汇");
            Level4luan_2.Click += new EventHandler(SelectBook_Click);
            ToolStripItem Level8_1 = new ToolStripMenuItem("专八真题高频词");
            Level8_1.Click += new EventHandler(SelectBook_Click);
            ToolStripItem Level8luan_2 = new ToolStripMenuItem("专八核心词汇");
            Level8luan_2.Click += new EventHandler(SelectBook_Click);
            ToolStripItem Goin = new ToolStripMenuItem("顺序五十音");
            Goin.Click += new EventHandler(SelectBook_Click);
            ToolStripItem StdJp_Mid = new ToolStripMenuItem("标准日本语中级词汇");
            StdJp_Mid.Click += new EventHandler(SelectBook_Click);
            ToolStripItem RandomWord = new ToolStripMenuItem("随机单词测试");
            RandomWord.Click += new EventHandler(RandomWordTest_Click);
            ToolStripItem RandomGoin = new ToolStripMenuItem("随机五十音测试");
            RandomGoin.Click += new EventHandler(RandomGoinTest_Click);
            ToolStripItem RandomJpWord = new ToolStripMenuItem("随机日语单词测试");
            RandomJpWord.Click += new EventHandler(RandomJpWordTest_Click);
            ToolStripItem Pdf = new ToolStripMenuItem("Star!!");
            Pdf.Click += new EventHandler(OpenPdf_Click);
            ToolStripItem Use = new ToolStripMenuItem("使用说明(必读)");
            Use.Click += new EventHandler(HowToUse_Click);
            ToolStripItem Site = new ToolStripMenuItem("官方网站");
            Site.Click += new EventHandler(Site_Click);
            ToolStripItem Shortcuts = new ToolStripMenuItem("快捷方式");
            Shortcuts.Click += new EventHandler(ShortCuts_Click);
            ToolStripItem ResetLearingStatus = new ToolStripMenuItem("重置进度");
            ResetLearingStatus.Click += new EventHandler(ResetLearingStatus_Click);


 




            if (Select.TABLE_NAME == "CET4_1")
                CET4_1.PerformClick();
            else if (Select.TABLE_NAME == "CET4_3")
                CET4_3.PerformClick();
            else if (Select.TABLE_NAME == "CET6_1")
                CET6_1.PerformClick();
            else if (Select.TABLE_NAME == "CET6_3")
                CET6_3.PerformClick();
            else if (Select.TABLE_NAME == "GMAT_3")
                GMAT_3.PerformClick();
            else if (Select.TABLE_NAME == "GRE_2")
                GRE_2.PerformClick();
            else if (Select.TABLE_NAME == "IELTS_3")
                IELTS_3.PerformClick();
            else if (Select.TABLE_NAME == "TOEFL_2")
                TOEFL_2.PerformClick();
            else if (Select.TABLE_NAME == "SAT_2")
                SAT_2.PerformClick();
            else if (Select.TABLE_NAME == "KaoYan_1")
                KaoYan_1.PerformClick();
            else if (Select.TABLE_NAME == "KaoYan_2")
                KaoYan_2.PerformClick();
            else if (Select.TABLE_NAME == "KaoYan_3")
                KaoYan_3.PerformClick();
            else if (Select.TABLE_NAME == "Level4_1")
                Level4_1.PerformClick();
            else if (Select.TABLE_NAME == "Level4luan_2")
                Level4luan_2.PerformClick();
            else if (Select.TABLE_NAME == "Level8_1")
                Level8_1.PerformClick();
            else if (Select.TABLE_NAME == "Level8luan_2")
                Level8luan_2.PerformClick();
            else if (Select.TABLE_NAME == "Goin")
                Goin.PerformClick();

            Cms.Items.Add(Begin);
            //Cms.Items.Add(SetNumber);
            //Cms.Items.Add(SetEngType);
            Cms.Items.Add(ImportWords);
            Cms.Items.Add(SelectBook);
            Cms.Items.Add(SelectJpBook);
            Cms.Items.Add(RandomTest);
            Cms.Items.Add(Settings);
            Cms.Items.Add(GotoHtml);
            Cms.Items.Add(Dashboard);
            Cms.Items.Add(Start);
            Cms.Items.Add(ExitMenuItem);

            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(CET4_1);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(CET4_3);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(CET6_1);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(CET6_3);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(GMAT_3);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(GRE_2);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(IELTS_3);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(TOEFL_2);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(SAT_2);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(KaoYan_1);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(KaoYan_2);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(KaoYan_3);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(Level4_1);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(Level4luan_2);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(Level8_1);
            ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems.Add(Level8luan_2);
            ((ToolStripDropDownItem)Cms.Items[3]).DropDownItems.Add(Goin);
            ((ToolStripDropDownItem)Cms.Items[3]).DropDownItems.Add(StdJp_Mid);
            ((ToolStripDropDownItem)Cms.Items[4]).DropDownItems.Add(RandomWord);
            ((ToolStripDropDownItem)Cms.Items[4]).DropDownItems.Add(RandomGoin);
            ((ToolStripDropDownItem)Cms.Items[4]).DropDownItems.Add(RandomJpWord);
            ((ToolStripDropDownItem)Cms.Items[5]).DropDownItems.Add(SetNumber);
            ((ToolStripDropDownItem)Cms.Items[5]).DropDownItems.Add(SetEngType);
            ((ToolStripDropDownItem)Cms.Items[5]).DropDownItems.Add(SetAutoPlay);
            ((ToolStripDropDownItem)Cms.Items[5]).DropDownItems.Add(SetAutoLog);
            ((ToolStripDropDownItem)Cms.Items[5]).DropDownItems.Add(SetAiEssay);
            ((ToolStripDropDownItem)Cms.Items[5]).DropDownItems.Add(SetAiModel);
            ((ToolStripDropDownItem)Cms.Items[5]).DropDownItems.Add(SetAiModels);
            ((ToolStripDropDownItem)Cms.Items[5]).DropDownItems.Add(SetAiProvider);
            ((ToolStripDropDownItem)Cms.Items[5]).DropDownItems.Add(SetAiKey);
            ((ToolStripDropDownItem)Cms.Items[5]).DropDownItems.Add(SetTestMode);
            ((ToolStripDropDownItem)Cms.Items[5]).DropDownItems.Add(ResetLearingStatus);
            
            ((ToolStripDropDownItem)Cms.Items[6]).DropDownItems.Add(Shortcuts);
            ((ToolStripDropDownItem)Cms.Items[6]).DropDownItems.Add(Use);
            ((ToolStripDropDownItem)Cms.Items[6]).DropDownItems.Add(Site);
            ((ToolStripDropDownItem)Cms.Items[6]).DropDownItems.Add(Pdf);
            ((ToolStripDropDownItem)Cms.Items[6]).DropDownItems.Add(CheckUpdate);            
        }

        // ───────────── 更新检查（2026-10-15 新增）─────────────

        /// <summary>托盘「检查更新」：手动检查，无更新或检查失败时给一次性提示</summary>
        private void CheckUpdate_Click(object sender, EventArgs e)
        {
            StartUpdateCheck(true);
        }

        /// <summary>
        /// 检查 GitHub 是否有新版本。
        /// 2026-10-18（3.4.3）起改成「一键更新」：确认后**直接下载安装包 → 静默安装 → 自动重启**，不再跳浏览器。
        /// manual=false：启动后延迟 15 秒静默检查，无更新 / 检查失败 / 正在学习或已有弹窗时什么都不做；
        /// manual=true：用户点「检查更新」，无更新或失败时给一次性提示。
        /// 任何异常都在此吞掉——更新检查绝不能干扰学习主流程。
        /// </summary>
        private void StartUpdateCheck(bool manual)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (!manual) Thread.Sleep(UPDATE_CHECK_DELAY_MS);

                    Model.UpdateChecker.UpdateInfo info = Model.UpdateChecker.FetchLatest();
                    if (info == null)
                    {
                        if (manual) NotificationForm.ShowMessage("检查更新", "检查失败，请稍后再试");
                        return;
                    }
                    if (!Model.UpdateChecker.IsNewer(info))
                    {
                        if (manual) NotificationForm.ShowMessage("检查更新",
                            "当前已是最新版本 v" + Model.UpdateChecker.CurrentVersionString);
                        return;
                    }

                    // 自动检查时避开学习进行中 / 已有弹窗，避免抢屏或误关当前弹窗
                    if (!manual && (PushWords.IsLearningActive || NotificationForm.Current != null)) return;

                    bool hasAsset = info.HasAsset;
                    string prompt = "发现新版本 " + info.Tag + "（当前 v" + Model.UpdateChecker.CurrentVersionString + "）。";
                    prompt += hasAsset
                        ? "是否立即更新？\n确认后会直接下载并安装，装完自动重启 ToastFish。"
                        : "是否前往 GitHub 下载？";

                    var task = NotificationForm.ShowWithButtons("检查更新", prompt,
                        hasAsset ? new[] { "立即更新", "稍后再说" } : new[] { "前往下载", "稍后再说" },
                        NotificationForm.TIMEOUT_DIALOG);
                    if (!(task.Wait(NotificationForm.TIMEOUT_DIALOG + 5000) && task.Result == 0)) return;

                    if (!hasAsset)
                    {
                        // Release 里没有安装包附件 → 退回旧行为，打开浏览器
                        try { Process.Start(info.Url); } catch { }
                        return;
                    }

                    RunOneClickUpdate(info);
                }
                catch (Exception ex)
                {
                    try { PopupForm.LogPopup("UPDATE", "检查更新异常：" + ex.Message); } catch { }
                }
            });
        }

        /// <summary>
        /// 一键更新（#104 / 3.4.3）：下载安装包 → 写一个「等本进程退出后静默安装并重启」的 PowerShell 脚本 → 关闭自己。
        /// 失败时弹窗说明原因，并提供「前往下载页」手动兜底。整段跑在后台线程，绝不阻塞 UI。
        /// </summary>
        private void RunOneClickUpdate(Model.UpdateChecker.UpdateInfo info)
        {
            string workDir = Path.Combine(Path.GetTempPath(), "ToastFishUpdate");
            string fileName = string.IsNullOrEmpty(info.AssetName) ? "ToastFish-Setup.exe" : info.AssetName;
            string setupPath = Path.Combine(workDir, fileName);

            try
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);   // 清掉上一次的残留
            }
            catch { }
            try { Directory.CreateDirectory(workDir); }
            catch (Exception ex)
            {
                ShowUpdateFailed(info, "无法创建临时目录：" + ex.Message);
                return;
            }

            var popup = NotificationForm.ShowProgressPopup("更新 ToastFish",
                "正在连接更新服务器…（" + info.Tag + "）");

            Model.UpdateDownloader.Result result = null;
            try
            {
                result = Model.UpdateDownloader.Download(info.AssetUrl, info.AssetApiUrl, setupPath,
                    (done, total, text) =>
                    {
                        int pct = total > 0 ? (int)(done * 100 / total) : -1;
                        popup.Report(pct, text);
                    },
                    null);
            }
            catch (Exception ex)
            {
                result = new Model.UpdateDownloader.Result { Ok = false, Error = ex.Message };
            }

            popup.Close();

            if (result == null || !result.Ok)
            {
                string reason = result == null ? "未知错误" : result.Error;
                ShowUpdateFailed(info, reason);
                return;
            }

            try
            {
                string installDir = GetInstallDir();
                string exePath = Path.Combine(installDir, "ToastFish.exe");
                string logPath = Path.Combine(workDir, "install.log");
                string scriptPath = Path.Combine(workDir, "apply_update.ps1");

                string script = Model.UpdateDownloader.BuildApplyScript(setupPath, exePath, logPath);
                File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(true));   // 带 BOM：PowerShell 才按 UTF-8 解析中文路径

                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = workDir
                };
                Process.Start(psi);
                try { PopupForm.LogPopup("UPDATE", "一键更新开始：" + info.Tag + " setup=" + setupPath + " installDir=" + installDir); } catch { }

                // 安装脚本会先等 2 秒再 taskkill 本进程；这里主动退出，让安装包能覆盖 ToastFish.exe
                var dispatcher = PushWords.UIDispatcher;
                if (dispatcher != null) dispatcher.BeginInvoke(new Action(() =>
                {
                    try { System.Windows.Application.Current.Shutdown(); } catch { }
                }));
            }
            catch (Exception ex)
            {
                ShowUpdateFailed(info, "启动安装程序失败：" + ex.Message);
            }
        }

        /// <summary>更新失败的统一提示：说明原因 + 提供「前往下载页」手动兜底</summary>
        private void ShowUpdateFailed(Model.UpdateChecker.UpdateInfo info, string reason)
        {
            try { PopupForm.LogPopup("UPDATE", "更新失败：" + reason); } catch { }
            string msg = "下载新版本失败：" + reason + "\n\n可以稍后重试，或前往 GitHub 手动下载。";
            try
            {
                var t = NotificationForm.ShowWithButtons("更新失败", msg, new[] { "前往下载页", "关闭" }, 60000);
                if (t.Wait(65000) && t.Result == 0)
                {
                    try { Process.Start(info.Url); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 当前安装目录：优先读安装器写的卸载注册表项 InstallLocation，
        /// 读不到就退回「本进程 exe 所在目录」（便携运行时也对）。
        /// </summary>
        private static string GetInstallDir()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}_is1"))
                {
                    if (key != null)
                    {
                        string loc = key.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(loc))
                        {
                            loc = loc.TrimEnd('\\');
                            if (loc.Length > 0) return loc;
                        }
                    }
                }
            }
            catch { }

            try
            {
                string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                if (!string.IsNullOrEmpty(exe)) return Path.GetDirectoryName(exe);
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToastFish");
        }

        private void Begin_Click(object sender, EventArgs e)
        {
            // 惊喜复习进行中时不允许开始学习，避免热键冲突
            if (SurpriseReviewManager.IsSurpriseInProgress)
                return;

            // 学习已在运行中：若弹窗被隐藏（Alt+H 手动隐藏，或单词/测验弹窗超时自动收起），
            // 由 Alt+Q 重新显示它、继续当前进度；确实没有可恢复的弹窗时给一句提示，避免"按了没反应"。
            if (PushWords.IsLearningActive)
            {
                // 还活着的弹窗（含 Alt+H 隐藏、超时收起）→ 重新显示，继续这一轮
                if (NotificationForm.ResumeCurrentPopup())
                    return;

                // #94（3.4.1）自愈：弹窗其实已经没了（被提示消息顶掉 / 被强行关闭），
                // 等待热键的线程会永久阻塞，只能重启软件。这里主动中断这一轮，
                // 等线程退出（finally 会把 IsLearningActive 复位）后再照常开新一轮。
                if (!NotificationForm.HasAnyLivePopup)
                {
                    PushWords.RequestAbortRound();
                    for (int i = 0; i < 60 && PushWords.IsLearningActive; i++)
                        Thread.Sleep(100);

                    if (PushWords.IsLearningActive)
                    {
                        // 等了 6 秒还没收尾：旧线程已经不在了（异常退出 / 卡在别的等待里），
                        // 强行复位标志，否则用户永远开不了新一轮（旧版只能重启软件）。
                        // RoundAbort 仍为 true，残留线程一旦醒来会立刻退出，不会长期和新一轮并存。
                        PushWords.IsLearningActive = false;
                        NotificationForm.ShowMessage("ToastFish", "上一轮已中断，正在重新开始…");
                    }
                }
                else if (NotificationForm.Current == null)
                {
                    NotificationForm.ShowMessage("ToastFish", "本轮复习仍在进行中，请稍候…");
                    return;
                }
                else
                {
                    // 弹窗存在但不可恢复（不是隐藏态）→ 说明用户正看着它，给一句提示即可
                    NotificationForm.ShowMessage("ToastFish", "本轮复习仍在进行中，请稍候…");
                    return;
                }
            }
            PushWords.RoundAbort = false;   // #94：新一轮开始前清掉上一轮的中断标记

            if (!System.IO.Directory.Exists("Log"))  {
                System.IO.Directory.CreateDirectory("Log");
            }
           // System.IO.Directory.CreateDirectory("Log");

            var state = thread.ThreadState;

            WordType Words = new WordType();
            Words.Number = Select.WORD_NUMBER;

            if (state == System.Threading.ThreadState.WaitSleepJoin || state == System.Threading.ThreadState.Stopped)
            {
                // #94：旧版是 while (状态 != Aborted) 无限等。线程如果早已正常结束（Stopped），
                // Abort() 是空操作、状态永远不会变成 Aborted → 这里会永久卡住。
                try { thread.Abort(); } catch { }
                for (int i = 0; i < 30; i++)
                {
                    var ts = thread.ThreadState;
                    if (ts == System.Threading.ThreadState.Aborted || ts == System.Threading.ThreadState.Stopped)
                        break;
                    Thread.Sleep(100);
                }
                if(Select.TABLE_NAME == "Goin")
                    thread = new Thread(new ParameterizedThreadStart(PushGoinWords.OrderGoin));
                else if(Select.TABLE_NAME == "StdJp_Mid")
                    thread = new Thread(new ParameterizedThreadStart(PushJpWords.Recitation));
                //else if (Select.TABLE_NAME == "自定义英语")
                //    thread = new Thread(new ParameterizedThreadStart(PushWords.Recitation));
                else
                    thread = new Thread(new ParameterizedThreadStart(PushWords.RecitationSM2));

                thread.Start(Words);
            }
            else
            {
                if (Select.TABLE_NAME == "Goin")
                    thread = new Thread(new ParameterizedThreadStart(PushGoinWords.OrderGoin));
                else if (Select.TABLE_NAME == "StdJp_Mid")
                    thread = new Thread(new ParameterizedThreadStart(PushJpWords.Recitation));
                //else if (Select.TABLE_NAME == "自定义英语")
                //    thread = new Thread(new ParameterizedThreadStart(PushWords.Recitation));
                else
                    thread = new Thread(new ParameterizedThreadStart(PushWords.RecitationSM2));
                
                thread.Start(Words);
            }
        }

        private void SetNumber_Click(object sender, EventArgs e)
        {
            Thread thread = new Thread(new ThreadStart(pushWords.SetWordNumber));
            thread.Start();
        }

        private void SetEngType_Click(object sender, EventArgs e)
        {
            Thread thread = new Thread(new ThreadStart(pushWords.SetEngType));
            thread.Start();
        }


        

        private void ImportWords_Click(object sender, EventArgs e)
        {
            OpenFileDialog Dialog = new OpenFileDialog();
            Dialog.Filter = "xlsx files (*.xlsx)|*.xlsx|xls files (*.xls)|*.xls";
            if (Dialog.ShowDialog() == System.Windows.Forms.DialogResult.Cancel)
            {
                return;
            }
            String FileName = Dialog.FileName;
            CreateLog Log = new CreateLog();
            WordType Words = new WordType();
            Words.Number = Select.WORD_NUMBER;
            object lstObj = Log.ImportExcel(FileName);
            string typeObj = lstObj.ToString();
            string typeWord= typeof(List<Word>).ToString();
            string typeJpWord = typeof(List<JpWord>).ToString();
            string typeCustWord = typeof(List<CustomizeWord>).ToString();
            try
            {
                if (typeObj == typeWord)
                {
                    Words.WordList = (List<Word>)lstObj;
                    Select.TABLE_NAME = "GRE_2";
                }
                else if (typeObj == typeJpWord)
                {
                    Words.JpWordList = (List<JpWord>)lstObj;
                    Select.TABLE_NAME = "StdJp_Mid";
                }
                else if (typeObj == typeCustWord)
                {
                    Words.CustWordList = (List<CustomizeWord>)lstObj;
                    Select.TABLE_NAME = "自定义";
                }
                else
                {
                    System.Windows.Forms.MessageBox.Show("导入文件出错！");
                    return;
                }
            }
            catch {
                System.Windows.Forms.MessageBox.Show("导入文件出错！");
                return;
            }
            
            if (!Directory.Exists("Log")){
                System.IO.Directory.CreateDirectory("Log");
            }
            

            var state = thread.ThreadState;

            if (state == System.Threading.ThreadState.WaitSleepJoin || state == System.Threading.ThreadState.Stopped)
            {
                // #94：旧版是 while (状态 != Aborted) 无限等。线程如果早已正常结束（Stopped），
                // Abort() 是空操作、状态永远不会变成 Aborted → 这里会永久卡住。
                try { thread.Abort(); } catch { }
                for (int i = 0; i < 30; i++)
                {
                    var ts = thread.ThreadState;
                    if (ts == System.Threading.ThreadState.Aborted || ts == System.Threading.ThreadState.Stopped)
                        break;
                    Thread.Sleep(100);
                }
                if (Select.TABLE_NAME == "Goin")
                    thread = new Thread(new ParameterizedThreadStart(PushGoinWords.OrderGoin));
                else if (Select.TABLE_NAME == "StdJp_Mid")
                    thread = new Thread(new ParameterizedThreadStart(PushJpWords.Recitation));
                //else if (Select.TABLE_NAME == "自定义英语")
                //    thread = new Thread(new ParameterizedThreadStart(PushWords.Recitation));
                else if (Select.TABLE_NAME == "自定义")
                    thread = new Thread(new ParameterizedThreadStart(PushCustomizeWords.Recitation));
                else
                    thread = new Thread(new ParameterizedThreadStart(PushWords.Recitation));

                thread.Start(Words);
            }
            else
            {
                if (Select.TABLE_NAME == "Goin")
                    thread = new Thread(new ParameterizedThreadStart(PushGoinWords.OrderGoin));
                else if (Select.TABLE_NAME == "StdJp_Mid")
                    thread = new Thread(new ParameterizedThreadStart(PushJpWords.Recitation));
                //else if (Select.TABLE_NAME == "自定义英语")
                //    thread = new Thread(new ParameterizedThreadStart(PushWords.Recitation));
                else if (Select.TABLE_NAME == "自定义")
                    thread = new Thread(new ParameterizedThreadStart(PushCustomizeWords.Recitation));
                else
                    thread = new Thread(new ParameterizedThreadStart(PushWords.Recitation));

                thread.Start(Words);
            }
        }

        private void SelectBook_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem curitem = sender as ToolStripMenuItem;
            if (curitem != null && curitem.OwnerItem !=null)
            {
                var Cms = (curitem.OwnerItem as ToolStripMenuItem).Owner as ContextMenuStrip;
                //int index = (curitem.OwnerItem as ToolStripMenuItem).DropDownItems.IndexOf(item);
                foreach (var itemi in ((ToolStripDropDownItem)Cms.Items[2]).DropDownItems)
                {
                    (itemi as ToolStripMenuItem).Checked = false;
                }
                foreach (var itemi in ((ToolStripDropDownItem)Cms.Items[3]).DropDownItems)
                {
                    (itemi as ToolStripMenuItem).Checked = false;
                }
            }
            curitem.Checked = true;
           // (sender as ToolStripMenuItem).Checked = !(sender as ToolStripMenuItem).Checked;
            string TempName = "";
            if (sender.ToString() == "四级核心词汇")
                TempName = "CET4_1";
            else if (sender.ToString() == "四级完整词汇")
                TempName = "CET4_3";
            else if (sender.ToString() == "六级核心词汇")
                TempName = "CET6_1";
            else if (sender.ToString() == "六级完整词汇")
                TempName = "CET6_3";
            else if (sender.ToString() == "GMAT词汇")
                TempName = "GMAT_3";
            else if (sender.ToString() == "GRE词汇")
                TempName = "GRE_2";
            else if (sender.ToString() == "IELTS词汇")
                TempName = "IELTS_3";
            else if (sender.ToString() == "TOEFL词汇")
                TempName = "TOEFL_2";
            else if (sender.ToString() == "SAT词汇")
                TempName = "SAT_2";
            else if (sender.ToString() == "考研必考词汇")
                TempName = "KaoYan_1";
            else if (sender.ToString() == "考研完整词汇")
                TempName = "KaoYan_2";
            else if (sender.ToString() == "考研词频词汇")
                TempName = "KaoYan_3";
            else if (sender.ToString() == "专四真题高频词")
                TempName = "Level4_1";
            else if (sender.ToString() == "专四核心词汇")
                TempName = "Level4luan_2";
            else if (sender.ToString() == "专八真题高频词")
                TempName = "Level8_1";
            else if (sender.ToString() == "专八核心词汇")
                TempName = "Level8luan_2";
            else if (sender.ToString() == "顺序五十音")
                TempName = "Goin";
            else if (sender.ToString() == "标准日本语中级词汇")
            {
                TempName = "StdJp_Mid";
                bool Flag = false;
                SpeechSynthesizer synth = new SpeechSynthesizer();
                foreach (InstalledVoice voice in synth.GetInstalledVoices())
                {
                    VoiceInfo info = voice.VoiceInfo;
                    if (info.Culture.IetfLanguageTag == "ja-JP")
                        Flag = true;
                }
                if(Flag == false)
                    System.Windows.Forms.MessageBox.Show("检测到您未安装日语语音包，请去“设置”->“时间和语言”->“语音”->“添加语音”中安装日本语，以免影响正常使用。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (sender.ToString() == "随机五十音测试")
                TempName = "Goin";
            // 切换词库 → 清空 AI 短文累积（累积的 wordRank 属于旧词库，切换后无意义，2026-09-16）
            if (Select.TABLE_NAME != TempName) Select.ClearPendingEssayWords();
            Select.TABLE_NAME = TempName;
            Se.UpdateBookName(TempName);
            Se.UpdateTableCount();
            //if (sender.ToString() == "顺序五十音")
            //{
            //     int Progress = Se.GetGoinProgress();
            //     PushWords.PushMessage("当前词库：" + sender.ToString() + "\n当前进度：" + Progress.ToString() + "/104");
            // }
            // else
            //{
            List<int> res = Se.SelectCount();
                pushWords.PushMessage("当前词库：" + sender.ToString() + "\n当前进度：" + res[0].ToString() + "/" + res[1].ToString());
           // }
        }

        private void RandomWordTest_Click(object sender, EventArgs e)
        {
            var state = thread.ThreadState;
            if (state == System.Threading.ThreadState.WaitSleepJoin || state == System.Threading.ThreadState.Stopped)
            {
                // #94：旧版是 while (状态 != Aborted) 无限等。线程如果早已正常结束（Stopped），
                // Abort() 是空操作、状态永远不会变成 Aborted → 这里会永久卡住。
                try { thread.Abort(); } catch { }
                for (int i = 0; i < 30; i++)
                {
                    var ts = thread.ThreadState;
                    if (ts == System.Threading.ThreadState.Aborted || ts == System.Threading.ThreadState.Stopped)
                        break;
                    Thread.Sleep(100);
                }
            }
            if (Select.TABLE_NAME == "StdJp_Mid" || Select.TABLE_NAME == "Goin")
                Select.TABLE_NAME = "GRE_2";
            thread = new Thread(new ParameterizedThreadStart(pushWords.UnorderWord));
            thread.Start(Select.WORD_NUMBER);
        }

        private void RandomGoinTest_Click(object sender, EventArgs e)
        {
            Select.TABLE_NAME = "Goin";
            Se.UpdateBookName("Goin");
            var state = thread.ThreadState;
            if (state == System.Threading.ThreadState.WaitSleepJoin || state == System.Threading.ThreadState.Stopped)
            {
                // #94：旧版是 while (状态 != Aborted) 无限等。线程如果早已正常结束（Stopped），
                // Abort() 是空操作、状态永远不会变成 Aborted → 这里会永久卡住。
                try { thread.Abort(); } catch { }
                for (int i = 0; i < 30; i++)
                {
                    var ts = thread.ThreadState;
                    if (ts == System.Threading.ThreadState.Aborted || ts == System.Threading.ThreadState.Stopped)
                        break;
                    Thread.Sleep(100);
                }
                if (Select.TABLE_NAME == "Goin")
                    thread = new Thread(new ParameterizedThreadStart(PushGoinWords.UnorderGoin));
                thread.Start(Select.WORD_NUMBER);
            }
            else
            {
                if (Select.TABLE_NAME == "Goin")
                    thread = new Thread(new ParameterizedThreadStart(PushGoinWords.UnorderGoin));
                thread.Start(Select.WORD_NUMBER);
            }
        }

        private void RandomJpWordTest_Click(object sender, EventArgs e)
        {
            var state = thread.ThreadState;
            if (state == System.Threading.ThreadState.WaitSleepJoin || state == System.Threading.ThreadState.Stopped)
            {
                // #94：旧版是 while (状态 != Aborted) 无限等。线程如果早已正常结束（Stopped），
                // Abort() 是空操作、状态永远不会变成 Aborted → 这里会永久卡住。
                try { thread.Abort(); } catch { }
                for (int i = 0; i < 30; i++)
                {
                    var ts = thread.ThreadState;
                    if (ts == System.Threading.ThreadState.Aborted || ts == System.Threading.ThreadState.Stopped)
                        break;
                    Thread.Sleep(100);
                }
            }
            Select.TABLE_NAME = "StdJp_Mid";
            thread = new Thread(new ParameterizedThreadStart(PushJpWords.UnorderWord));
            thread.Start(Select.WORD_NUMBER);
        }

        public  void ResetLearingStatus_Click(object sender, EventArgs e)
        {
           string TableName;
           bool isok= TablelDictionary.TryGetValue(Select.TABLE_NAME, out TableName);
            if (!isok) {
                return;
            }
            
            DialogResult result = System.Windows.Forms.MessageBox.Show(
            $"是否要重置“{TableName}”的学习进度?", $"进度重置：{Select.TABLE_NAME}",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result==System.Windows.Forms.DialogResult.Yes)
            {
               
                try {
                    Se.ResetTableCount();
                    pushWords.PushMessage($"重置{TableName}完成！");
                    //System.Windows.Forms.MessageBox.Show($"重置{TableName}完成！");

                }
                catch {
                    pushWords.PushMessage($"重置{TableName}出错！");
                   // System.Windows.Forms.MessageBox.Show($"重置{TableName}出错！");
                }                
            }
            //this.Se. 
        }
        private void ShortCuts_Click(object sender, EventArgs e)
        {
            System.Windows.Forms.MessageBox.Show(
                "Alt+Q     开始一轮学习（再按一次暂停 / 恢复）" + "\n" +
                "Alt+1     没印象（Again）" + "\n" +
                "Alt+2     模糊（Hard）" + "\n" +
                "Alt+3     记住（Good）" + "\n" +
                "Alt+4     牢记（Easy）" + "\n" +
                "Alt+~     重播当前单词发音" + "\n" +
                "Alt+H     显示 / 隐藏当前弹窗" + "\n" +
                "按住 Alt   把当前弹窗等比放大一倍（松开还原）" + "\n\n" +
                "不想用键盘时，直接点击弹窗上的按钮也一样。" + "\n" +
                "弹窗右上角的 A+ 小图标可以关掉「按住 Alt 放大」。",
                "ToastFish 快捷键");
        }

        private void AutoPlay_Click(object sender, EventArgs e)
        {
            //sender as ToolStripMenuItem).Checked = !(sender as ToolStripMenuItem).Checked;
            if (Select.AUTO_PLAY == 0)
            {
                Select.AUTO_PLAY = 1;
                (sender as ToolStripMenuItem).Checked = true;
            }               
            else
            {
                Select.AUTO_PLAY = 0;
                (sender as ToolStripMenuItem).Checked = false;
            }
            Se.UpdateGlobalConfig();
        }

        /// <summary>学后阅读模式切换：例句串读 ↔ AI 短文（首次开启时若无 Key 先引导输入）</summary>
        private void AiEssay_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            if (Model.Ai.AiConfig.ReadingMode == 1)
            {
                Model.Ai.AiConfig.ReadingMode = 0;
                item.Checked = false;
                NotificationForm.ShowMessage("ToastFish", "学后阅读已切换为：例句串读");
            }
            else
            {
                if (string.IsNullOrEmpty(Model.Ai.AiConfig.ApiKey) && !PromptAiKey())
                {
                    NotificationForm.ShowMessage("ToastFish", "未设置 API Key，保持例句串读模式");
                    return;
                }
                Model.Ai.AiConfig.ReadingMode = 1;
                item.Checked = true;
                NotificationForm.ShowMessage("ToastFish", "学后阅读已切换为：AI 短文（生成失败时自动回退例句串读）");
            }
            Model.Ai.AiConfig.Save();
        }

        /// <summary>AI 模型切换：快速（flash，秒级）↔ 推理（pro 深度思考，约1~2分钟，不限字数质量更高）</summary>
        private void AiModel_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            if (Model.Ai.AiConfig.ModelMode == 1)
            {
                Model.Ai.AiConfig.ModelMode = 0;
                item.Checked = false;
                NotificationForm.ShowMessage("ToastFish", "AI 短文已切换为：快速模型（" + Model.Ai.AiConfig.Model + "，秒级生成）");
            }
            else
            {
                Model.Ai.AiConfig.ModelMode = 1;
                item.Checked = true;
                NotificationForm.ShowMessage("ToastFish", "AI 短文已切换为：推理模型（" + Model.Ai.AiConfig.ReasonModel + "，深度思考约1~2分钟，不限字数）");
            }
            Model.Ai.AiConfig.Save();
        }

        private void AiKey_Click(object sender, EventArgs e)
        {
            if (PromptAiKey())
                NotificationForm.ShowMessage("ToastFish", Model.Ai.AiConfig.ProviderName + " API Key 已保存");
        }

        /// <summary>测试模式切换：学完自动恢复数据库，不影响真实学习记录。</summary>
        private void TestMode_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            item.Checked = !item.Checked;
            PushWords.TestMode = item.Checked;
            string msg = item.Checked
                ? "测试模式已开启 — 本轮学习数据将在结束后自动恢复，不会保存"
                : "测试模式已关闭 — 恢复正常记录";
            NotificationForm.ShowMessage("ToastFish", msg);
        }

        /// <summary>构造一个「设置 AI 模型名」子菜单项，Tag 标记是快速模型还是推理模型。</summary>
        private System.Windows.Forms.ToolStripMenuItem MakeModelItem(string text, bool reason)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem();
            item.Text = text;
            item.Tag = reason;
            item.Click += new EventHandler(AiModelName_Click);
            return item;
        }

        /// <summary>修改当前服务商的快速/推理模型名。填的是 Model ID（如 kimi-k2.6 / k3-256k），改动按服务商分别记忆。</summary>
        private void AiModelName_Click(object sender, EventArgs e)
        {
            var item = sender as System.Windows.Forms.ToolStripMenuItem;
            if (item == null) return;
            bool reason = item.Tag is bool && (bool)item.Tag;
            string title = reason ? "推理模型名" : "快速模型名";
            string current = reason ? Model.Ai.AiConfig.ReasonModel : Model.Ai.AiConfig.Model;
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入 " + Model.Ai.AiConfig.ProviderName + " 的" + title
                + "（填 Model ID，例如 kimi-k2.6 / k3-256k / deepseek-v4-pro）：\n当前：" + current,
                "ToastFish - AI 模型名", current);
            if (string.IsNullOrWhiteSpace(input)) return;
            if (reason) Model.Ai.AiConfig.ReasonModel = input.Trim();
            else Model.Ai.AiConfig.Model = input.Trim();
            Model.Ai.AiConfig.Save();
            RefreshAiModelMenuText();
            NotificationForm.ShowMessage("ToastFish",
                Model.Ai.AiConfig.ProviderName + " 的" + title + "已设置为：" + input.Trim());
        }

        /// <summary>同步「设置 AI 模型名」子菜单上显示的当前模型名。</summary>
        private void RefreshAiModelMenuText()
        {
            SetAiFastModel.Text = "快速模型名（" + Model.Ai.AiConfig.Model + "）";
            SetAiReasonModel.Text = "推理模型名（" + Model.Ai.AiConfig.ReasonModel + "）";
        }

        /// <summary>构造一个「AI 服务商」子菜单项，Tag 保存服务商编号。</summary>
        private System.Windows.Forms.ToolStripMenuItem MakeProviderItem(string text, int provider)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem();
            item.Text = text;
            item.Tag = provider;
            item.Checked = (Model.Ai.AiConfig.Provider == provider);
            item.Click += new EventHandler(AiProvider_Click);
            return item;
        }

        /// <summary>切换 AI 服务商：套用预设端点与模型；自定义服务商则逐个询问地址与模型名。</summary>
        private void AiProvider_Click(object sender, EventArgs e)
        {
            var item = sender as System.Windows.Forms.ToolStripMenuItem;
            if (item == null || item.Tag == null) return;
            int provider = (int)item.Tag;

            // 必须先切换、再询问：切换会把该服务商上次填过的端点/模型还原出来，
            // 作为输入框的默认值；顺序颠倒会导致自定义预填上一个服务商的地址
            Model.Ai.AiConfig.ApplyProvider(provider);

            if (provider == Model.Ai.AiConfig.ProviderCustom)
            {
                // 自定义：输入框默认值 = 该服务商上次填过的值，留空或取消即保持不变
                string url = Microsoft.VisualBasic.Interaction.InputBox(
                    "接口地址（OpenAI 兼容，结尾的 /v1 要带上）：", "AI 服务商 - 自定义", Model.Ai.AiConfig.BaseUrl);
                if (!string.IsNullOrWhiteSpace(url)) Model.Ai.AiConfig.BaseUrl = url.Trim();
                string fast = Microsoft.VisualBasic.Interaction.InputBox(
                    "快速模型名（默认用它，要求响应快）：", "AI 服务商 - 自定义", Model.Ai.AiConfig.Model);
                if (!string.IsNullOrWhiteSpace(fast)) Model.Ai.AiConfig.Model = fast.Trim();
                string reasoning = Microsoft.VisualBasic.Interaction.InputBox(
                    "推理模型名（勾选「AI短文用推理模型」时用它）：", "AI 服务商 - 自定义", Model.Ai.AiConfig.ReasonModel);
                if (!string.IsNullOrWhiteSpace(reasoning)) Model.Ai.AiConfig.ReasonModel = reasoning.Trim();
                Model.Ai.AiConfig.Save();
            }

            // 勾选态与菜单文字同步
            foreach (System.Windows.Forms.ToolStripItem drop in SetAiProvider.DropDownItems)
            {
                var mi = drop as System.Windows.Forms.ToolStripMenuItem;
                if (mi != null && mi.Tag != null) mi.Checked = ((int)mi.Tag == provider);
            }
            SetAiProvider.Text = "AI 服务商（" + Model.Ai.AiConfig.ProviderName + "）";
            SetAiKey.Text = "设置 " + Model.Ai.AiConfig.ProviderName + " Key";
            RefreshAiModelMenuText();

            if (string.IsNullOrWhiteSpace(Model.Ai.AiConfig.ApiKey))
            {
                // 该服务商还没存过 Key，直接引导输入
                if (PromptAiKey())
                    NotificationForm.ShowMessage("ToastFish", Model.Ai.AiConfig.ProviderName + " API Key 已保存");
                return;
            }
            NotificationForm.ShowMessage("ToastFish", "AI 服务商已切换为：" + Model.Ai.AiConfig.ProviderName
                + "\n模型：" + Model.Ai.AiConfig.Model);
        }

        /// <summary>弹输入框设置当前服务商的 API Key，成功保存返回 true</summary>
        private bool PromptAiKey()
        {
            string hint;
            switch (Model.Ai.AiConfig.Provider)
            {
                case Model.Ai.AiConfig.ProviderKimi:
                    hint = "请输入 Kimi 开放平台 API Key：\n获取地址：https://platform.kimi.com/console/api-keys";
                    break;
                case Model.Ai.AiConfig.ProviderKimiCode:
                    hint = "请输入 Kimi Code 订阅 API Key：\n获取地址：https://www.kimi.com/code/console\n（订阅 Key 与开放平台 Key 不通用，混用会 401）";
                    break;
                case Model.Ai.AiConfig.ProviderCustom:
                    hint = "请输入自定义服务商的 API Key：\n（接口地址与模型名由「AI 服务商」菜单设置）";
                    break;
                default:
                    hint = "请输入 DeepSeek API Key（sk- 开头）：\n获取地址：https://platform.deepseek.com";
                    break;
            }
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                hint, "ToastFish - AI 短文设置", Model.Ai.AiConfig.ApiKey);
            if (string.IsNullOrWhiteSpace(input))
                return false;
            Model.Ai.AiConfig.ApiKey = input.Trim();
            Model.Ai.AiConfig.Save();
            return true;
        }

        private void AutoLog_Click(object sender, EventArgs e)
        {
            //(sender as ToolStripMenuItem).Checked = !(sender as ToolStripMenuItem).Checked;
            if (Select.AUTO_LOG == 0)
            {
                Select.AUTO_LOG = 1;
                (sender as ToolStripMenuItem).Checked = true;
            }
            else
            {
                Select.AUTO_LOG = 0;
                (sender as ToolStripMenuItem).Checked = false;
            }
            Se.UpdateGlobalConfig();
        }

        private void HowToUse_Click(object sender, EventArgs e)
        {
            OpenWithBrowser(PathHelper.ExeDir + @"\Resources\使用说明.html");
        }
        private void Dashboard_Click(object sender, EventArgs e)
        {
            // 先重新生成仪表盘，反映当前词库的最新进度（2026-09-05）。
            // 否则切换词库后打开会显示旧的 dashboard.html（仍是上一个词库的数据）。
            PushWords.RefreshDashboard(15000);

            string dashboardPath = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "Resources", "dashboard.html");
            if (System.IO.File.Exists(dashboardPath))
            {
                OpenWithBrowser(dashboardPath);
                return;
            }

            // issue #2：旧版这里只有 if(Exists) 没有 else —— 新电脑没有 Python 时
            // 用户只看到一个转圈的鼠标指针，完全不知道发生了什么。现在明确告知。
            string scriptPath = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "Resources", "generate_dashboard.py");
            System.Windows.Forms.MessageBox.Show(
                "学习仪表盘生成失败，无法打开。\n\n" +
                "仪表盘需要 Python 3 才能把学习记录渲染成网页，本机没有找到可用的 Python 运行时。\n\n" +
                "解决办法：安装 Python 3（https://www.python.org/downloads/ ，安装时务必勾选 " +
                "\"Add python.exe to PATH\"），然后重新点击本菜单。\n\n" +
                "生成脚本：" + scriptPath,
                "学习报告", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
        }
        private void OpenWithBrowser(string path)
        {
            // 显式指定浏览器：Edge → Chrome → cmd/start 兜底
            // 不用 Process.Start(path) 因为它依赖系统默认 .html 关联，可能未设置
            string[][] browsers = new string[][] {
                new string[] { "msedge", "\"" + path + "\"" },
                new string[] { "chrome", "\"" + path + "\"" },
            };
            foreach (var b in browsers)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(b[0], b[1])
                    {
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                    return;
                }
                catch { }
            }
            // 最后兜底：cmd /c start，Windows 自行处理
            try
            {
                System.Diagnostics.Process.Start("cmd", "/c start \"\" \"" + path + "\"");
            }
            catch (System.Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    "无法打开浏览器。请手动打开：\n" + path + "\n\n错误：" + ex.Message,
                    "学习报告", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
        }
        private void Site_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://lab.magiconch.com/toast-fish/");
        }
        private void OpenPdf_Click(object sender, EventArgs e)
        {
            OpenWithBrowser(PathHelper.ExeDir + @"\Resources\Star.pdf");
        }
        private void ExitApp_Click(object sender, EventArgs e)
        {
            // 停止惊喜复习监控
            SurpriseReviewManager.Stop();

            // 退出收尾：统计本次学习分数 + 写入 StudyLog（2026-09-16 修复）
            // 此前这里直接 Environment.Exit(0)，会绕过 Application.OnExit，导致
            // App.OnExit 中的 PostLearnApply()/LogStudySession() 从不执行，
            // StudyLog 表长期无记录（仪表盘统计缺数据）。与 App.OnExit 逻辑保持一致。
            try
            {
                var result = Select.PostLearnApply();
                if (result.TotalWords > 0)
                {
                    string exeDir = PathHelper.ExeDir;
                    using (var db = new System.Data.SQLite.SQLiteConnection(
                        $"Data Source={System.IO.Path.Combine(exeDir, "Resources", "inami.db")};Version=3"))
                    {
                        db.Open();
                        Model.StudyLog.StudyLogManager.LogStudySession(db, Select.TABLE_NAME,
                            result.TotalWords, result.EasyCount, result.GoodCount,
                            result.HardCount, result.AgainCount);
                    }
                }
            }
            catch { }

            // ToastNotificationManagerCompat.History.Clear();
            Environment.Exit(0);
        }

        private void Start_Click(object sender, EventArgs e)
        {
            bool newState = !(sender as ToolStripMenuItem).Checked;
            (sender as ToolStripMenuItem).Checked = newState;

            if (newState)
            {
                AutoStartHelper.Enable();
                // 同时清理旧的启动文件夹快捷方式（迁移到注册表方式）
                string oldLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ToastFish.lnk");
                try { if (File.Exists(oldLnk)) File.Delete(oldLnk); } catch { }
            }
            else
            {
                AutoStartHelper.Disable();
            }
        }
        #endregion
    }
}

