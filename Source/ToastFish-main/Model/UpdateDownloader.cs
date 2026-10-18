using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace ToastFish.Model
{
    /// <summary>
    /// 一键更新的下载器（2026-10-18 新增，#104 / 3.4.3）。
    /// 只负责「把安装包拉到本地」，不碰 UI；进度和失败原因通过回调 / 返回值交给调用方。
    /// 失败一律转成中文可读文案（用户看到的是「无法连接更新服务器」而不是「基础连接已经关闭」）。
    /// </summary>
    public static class UpdateDownloader
    {
        /// <summary>下载结果</summary>
        public class Result
        {
            public bool Ok;          // 是否成功
            public string Error;     // 失败原因（中文，可直接展示给用户）
            public string Path;      // 成功时的本地安装包路径
            public bool Canceled;    // 是否被用户取消
        }

        /// <summary>进度回调：已下载字节 / 总字节（&lt;=0 表示未知）/ 现成的一行文案</summary>
        public delegate void ProgressHandler(long done, long total, string text);

        /// <summary>
        /// 下载安装包。先走浏览器直链，失败再走资产 API 备用通道（两个域名不同，国内网络常常只有一个通）。
        /// </summary>
        /// <param name="url">首选地址（browser_download_url）</param>
        /// <param name="fallbackUrl">备用地址（资产 API url），可为空</param>
        /// <param name="destPath">保存到本地的完整路径</param>
        /// <param name="progress">进度回调，可为 null</param>
        /// <param name="canceled">返回 true 表示用户取消，可为 null</param>
        public static Result Download(string url, string fallbackUrl, string destPath,
            ProgressHandler progress, Func<bool> canceled)
        {
            Result first = TryDownload(url, destPath, progress, canceled, false);
            if (first.Ok || first.Canceled) return first;

            if (!string.IsNullOrEmpty(fallbackUrl))
            {
                if (progress != null) progress(0, 0, "直连失败，改用备用通道…");
                Result second = TryDownload(fallbackUrl, destPath, progress, canceled, true);
                if (second.Ok || second.Canceled) return second;
                second.Error = second.Error + "\n（直连：" + first.Error + "）";
                return second;
            }
            return first;
        }

        private static Result TryDownload(string url, string destPath, ProgressHandler progress,
            Func<bool> canceled, bool useApiAccept)
        {
            if (string.IsNullOrEmpty(url))
                return new Result { Ok = false, Error = "没有找到安装包下载地址" };

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }

            FileStream fs = null;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "ToastFish-Updater";
                req.Accept = useApiAccept ? "application/octet-stream" : "application/octet-stream";
                req.Timeout = 20000;              // 连接超时
                req.ReadWriteTimeout = 60000;     // 单次读取超时（大文件慢速网络留余量）
                req.AllowAutoRedirect = true;     // github.com → objects.githubusercontent.com
                req.KeepAlive = false;
                req.AutomaticDecompression = DecompressionMethods.None;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    long total = resp.ContentLength;
                    Stream src = resp.GetResponseStream();
                    fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
                    byte[] buf = new byte[81920];
                    long done = 0;
                    Stopwatch sw = Stopwatch.StartNew();
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (canceled != null && canceled())
                            return new Result { Ok = false, Canceled = true, Error = "已取消" };
                        fs.Write(buf, 0, n);
                        done += n;
                        if (sw.ElapsedMilliseconds >= 200)
                        {
                            sw.Restart();
                            if (progress != null) progress(done, total, Describe(done, total));
                        }
                    }
                    fs.Flush();
                    fs.Dispose();
                    fs = null;

                    if (total > 0 && done != total)
                        return new Result { Ok = false, Error = "下载不完整（" + done + "/" + total + " 字节）" };

                    if (progress != null) progress(done, total > 0 ? total : done, "下载完成");
                    return new Result { Ok = true, Path = destPath };
                }
            }
            catch (Exception ex)
            {
                try { if (fs != null) fs.Dispose(); } catch { }
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                return new Result { Ok = false, Error = Describe(ex) };
            }
        }

        /// <summary>组装进度文案：已下载 12.3 / 33.7 MB（36%）</summary>
        private static string Describe(long done, long total)
        {
            double mb = done / 1048576.0;
            if (total <= 0) return "已下载 " + mb.ToString("0.0") + " MB";
            double tmb = total / 1048576.0;
            int pct = (int)(done * 100 / total);
            if (pct > 100) pct = 100;
            return "已下载 " + mb.ToString("0.0") + " / " + tmb.ToString("0.0") + " MB（" + pct + "%）";
        }

        /// <summary>把网络异常翻译成用户看得懂的中文（不要直接抛英文异常信息）</summary>
        private static string Describe(Exception ex)
        {
            var we = ex as WebException;
            if (we != null)
            {
                switch (we.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure:
                        return "域名解析失败（DNS），请检查网络";
                    case WebExceptionStatus.ConnectFailure:
                        return "无法连接更新服务器（github.com），请检查网络或代理";
                    case WebExceptionStatus.Timeout:
                        return "连接更新服务器超时，请稍后重试";
                    case WebExceptionStatus.TrustFailure:
                        return "HTTPS 证书校验失败（可能有代理或安全软件拦截）";
                    case WebExceptionStatus.ReceiveFailure:
                    case WebExceptionStatus.ConnectionClosed:
                    case WebExceptionStatus.KeepAliveFailure:
                        return "下载过程中连接中断";
                    case WebExceptionStatus.ProtocolError:
                        var resp = we.Response as HttpWebResponse;
                        if (resp != null)
                            return "服务器返回 " + (int)resp.StatusCode + " " + resp.StatusDescription;
                        break;
                }
                return "网络错误：" + we.Status;
            }

            var io = ex as IOException;
            if (io != null) return "写入文件失败：" + io.Message;
            return ex.Message;
        }

        /// <summary>
        /// 生成「等本进程退出 → 静默安装 → 重新拉起」的 PowerShell 脚本。
        /// 为什么不在本进程里直接跑安装器：ToastFish.exe 正在运行，安装器第一步就 taskkill 掉它，
        /// 中途退出会留下半装状态；交给独立进程、等 2 秒再装最稳。
        /// 为什么用 .ps1 而不是 .cmd：cmd 批处理里的中文/非 ASCII 路径会按当前代码页解析而乱码，
        /// PowerShell 按 UTF-8 BOM 读脚本，带中文用户名的路径也不会出错。
        /// </summary>
        public static string BuildApplyScript(string setupPath, string exePath, string logPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            sb.AppendLine("Start-Sleep -Seconds 2");
            sb.AppendLine("Stop-Process -Name ToastFish -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("Start-Sleep -Seconds 2");
            sb.AppendLine("$installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/LOG=' + " + Quote(logPath) + ")");
            sb.AppendLine("Start-Process -FilePath " + Quote(setupPath) + " -ArgumentList $installArgs -Wait");
            sb.AppendLine("Start-Sleep -Seconds 2");
            sb.AppendLine("if (Test-Path -LiteralPath " + Quote(exePath) + ") { Start-Process -FilePath " + Quote(exePath) + " }");
            return sb.ToString();
        }

        /// <summary>PowerShell 单引号字符串字面量（单引号内部的两个单引号转义）</summary>
        private static string Quote(string s)
        {
            string t = s == null ? "" : s.Replace("'", "''");
            return "'" + t + "'";
        }
    }
}
