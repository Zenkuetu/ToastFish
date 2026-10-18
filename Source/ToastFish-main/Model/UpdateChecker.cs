using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace ToastFish.Model
{
    /// <summary>
    /// GitHub 更新检查器（2026-10-15 新增）。
    /// 设计原则：本类只负责「取数据 + 判断新旧」，不碰任何 UI；任何异常/超时/离线/解析失败
    /// 一律返回 null 且不抛出。是否提示、何时提示由调用方（ToastFish.xaml.cs）决定——
    /// 自动检查路径在「无更新」和「检查失败」时都必须完全静默。
    /// </summary>
    public static class UpdateChecker
    {
        public const string REPO_OWNER = "Zenkuetu";
        public const string REPO_NAME = "ToastFish";

        private const string LATEST_API =
            "https://api.github.com/repos/" + REPO_OWNER + "/" + REPO_NAME + "/releases/latest";
        private const int TIMEOUT_MS = 8000;

        /// <summary>最新 Release 解析结果</summary>
        public class UpdateInfo
        {
            public Version Version;   // 归一化后的 3 段版本（Major.Minor.Build）
            public string Tag;        // 原始 tag（如 v3.4.0）
            public string Url;        // Release 页面地址
            public string Notes;      // Release notes 正文（body）

            // #104（3.4.3）：一键更新需要的安装包信息
            public string AssetName;    // 安装包文件名（如 ToastFish-v3.4.3-Setup.exe）
            public string AssetUrl;     // 浏览器直链（github.com/.../releases/download/...）
            public string AssetApiUrl;  // 资产 API（带 Accept: application/octet-stream 同样能下）
            public long AssetSize;      // 安装包字节数（0 = 未知）

            /// <summary>是否拿到了可直接下载的安装包</summary>
            public bool HasAsset
            {
                get { return !string.IsNullOrEmpty(AssetUrl) || !string.IsNullOrEmpty(AssetApiUrl); }
            }
        }

        /// <summary>当前程序版本（取 AssemblyVersion，如 3.3.1.0 → 归一化为 3.3.1）</summary>
        public static Version CurrentVersion
        {
            get
            {
                try { return Normalize(typeof(UpdateChecker).Assembly.GetName().Version); }
                catch { return new Version(0, 0, 0); }
            }
        }

        /// <summary>当前版本的 3 段字符串（如 "3.3.1"）</summary>
        public static string CurrentVersionString
        {
            get
            {
                Version v = CurrentVersion;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        /// <summary>
        /// 拉取 GitHub 最新 Release（/releases/latest 只返回非草稿、非预发布的最新版）。
        /// 网络错误 / 超时 / 404 / 限流 / JSON 异常 → 返回 null，绝不抛异常。
        /// </summary>
        public static UpdateInfo FetchLatest()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }

            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(LATEST_API);
                req.UserAgent = "ToastFish-UpdateChecker";
                req.Accept = "application/vnd.github+json";
                req.Timeout = TIMEOUT_MS;
                req.ReadWriteTimeout = TIMEOUT_MS;
                req.KeepAlive = false;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    if (string.IsNullOrEmpty(json)) return null;

                    Dictionary<string, object> dict =
                        new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                    if (dict == null) return null;

                    object raw;
                    if (dict.TryGetValue("draft", out raw) && Convert.ToBoolean(raw)) return null;
                    if (dict.TryGetValue("prerelease", out raw) && Convert.ToBoolean(raw)) return null;

                    string tag = dict.TryGetValue("tag_name", out raw) ? Convert.ToString(raw) : null;
                    if (string.IsNullOrEmpty(tag)) return null;

                    Version ver;
                    if (!TryParseTag(tag, out ver)) return null;

                    var info = new UpdateInfo
                    {
                        Version = ver,
                        Tag = tag,
                        Url = dict.TryGetValue("html_url", out raw) ? Convert.ToString(raw) : "",
                        Notes = dict.TryGetValue("body", out raw) ? Convert.ToString(raw) : ""
                    };

                    FillSetupAsset(info, dict);
                    if (!info.HasAsset) FillAssetFromText(info, json);
                    return info;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 从 release 的 assets[] 里挑出安装包：优先名字以 Setup.exe 结尾的，其次任意 .exe。
        /// AssetUrl 是浏览器直链（github.com/releases/download/... 会 302 到 objects.githubusercontent.com），
        /// AssetApiUrl 是资产 API（带 Accept: application/octet-stream 也能下）。
        /// 两个域名在国内网络里经常只有一个能连通，所以两个都留着，下载时互为备用通道。
        /// </summary>
        private static void FillSetupAsset(UpdateInfo info, Dictionary<string, object> dict)
        {
            object raw;
            if (!dict.TryGetValue("assets", out raw)) return;
            // 注意：JavaScriptSerializer 解析嵌套数组返回的是 ArrayList，不是 object[]，
            // 写成 raw as object[] 会永远是 null（3.4.3 首测就踩了这个坑：HasAsset 恒为 false）。
            System.Collections.IList assets = raw as System.Collections.IList;
            if (assets == null || assets.Count == 0) return;

            Dictionary<string, object> setup = null;
            Dictionary<string, object> anyExe = null;
            foreach (object o in assets)
            {
                var a = o as Dictionary<string, object>;
                if (a == null) continue;
                string name = a.TryGetValue("name", out raw) ? Convert.ToString(raw) : "";
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (anyExe == null) anyExe = a;
                if (name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)) { setup = a; break; }
            }

            Dictionary<string, object> pick = setup != null ? setup : anyExe;
            if (pick == null) return;

            info.AssetName = pick.TryGetValue("name", out raw) ? Convert.ToString(raw) : "";
            info.AssetUrl = pick.TryGetValue("browser_download_url", out raw) ? Convert.ToString(raw) : "";
            info.AssetApiUrl = pick.TryGetValue("url", out raw) ? Convert.ToString(raw) : "";
            long size = 0;
            if (pick.TryGetValue("size", out raw))
            {
                try { size = Convert.ToInt64(raw); } catch { size = 0; }
            }
            info.AssetSize = size;
        }

        /// <summary>
        /// 正则兜底：万一 assets 的结构再次变化（或解析器行为不同），直接从原始 JSON 文本里抠出
        /// 安装包直链、资产 API 与大小。GitHub 的 Release JSON 里这几条都带唯一前缀，误匹配风险极低。
        /// </summary>
        private static void FillAssetFromText(UpdateInfo info, string json)
        {
            try
            {
                Match m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+?\\.exe)\"");
                if (!m.Success) return;
                info.AssetUrl = m.Groups[1].Value;
                info.AssetName = info.AssetUrl.Substring(info.AssetUrl.LastIndexOf('/') + 1);

                Match api = Regex.Match(json, "\"url\"\\s*:\\s*\"(https://api\\.github\\.com/repos/[^\"]+/releases/assets/\\d+)\"");
                if (api.Success) info.AssetApiUrl = api.Groups[1].Value;

                Match size = Regex.Match(json, "\"size\"\\s*:\\s*(\\d+)");
                if (size.Success)
                {
                    long v;
                    if (long.TryParse(size.Groups[1].Value, out v)) info.AssetSize = v;
                }
            }
            catch { }
        }

        /// <summary>info 是否比当前版本更新（info 为 null 时返回 false）</summary>
        public static bool IsNewer(UpdateInfo info)
        {
            return info != null && info.Version != null && info.Version > CurrentVersion;
        }

        /// <summary>归一化为 3 段版本，避免 3.3.1（tag）与 3.3.1.0（AssemblyVersion）比较不等</summary>
        private static Version Normalize(Version v)
        {
            if (v == null) return new Version(0, 0, 0);
            int minor = v.Minor < 0 ? 0 : v.Minor;
            int build = v.Build < 0 ? 0 : v.Build;
            return new Version(v.Major, minor, build);
        }

        /// <summary>解析 tag（"v3.3.1" / "3.3.1"；带 "-beta" 等后缀的预发布只取数字段）</summary>
        private static bool TryParseTag(string tag, out Version ver)
        {
            ver = null;
            if (string.IsNullOrEmpty(tag)) return false;

            string s = tag.Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);

            int dash = s.IndexOf('-');           // 预发布后缀不参与比较
            if (dash >= 0) s = s.Substring(0, dash);

            Version parsed;
            if (!Version.TryParse(s, out parsed)) return false;

            ver = Normalize(parsed);
            return true;
        }
    }
}
