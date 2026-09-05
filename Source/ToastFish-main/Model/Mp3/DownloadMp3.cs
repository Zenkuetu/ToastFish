using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToastFish.Model.Mp3;

namespace ToastFish.Model.Download
{
    class DownloadMp3
    {
        /// <summary>
        /// http下载文件
        /// </summary>
        /// <param name="url">下载文件地址</param>
        /// <param name="path">文件存放地址，包含文件名</param>
        /// <returns></returns>
        private static readonly string ExeDir = System.IO.Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location);

        /// <summary>下载锁：串行化 MP3 下载，避免并发写同一临时文件。</summary>
        private static readonly object _dlLock = new object();

        public bool HttpDownload(string Url, string Name)
        {
            string CachePath = ExeDir + @"\Mp3Cache";
            if (!System.IO.Directory.Exists(CachePath))
            {
                System.IO.Directory.CreateDirectory(CachePath);
            }
            string finalPath = CachePath + @"\" + Name + ".mp3";
            string tmpPath = CachePath + @"\" + Name + ".mp3.tmp";   // 临时文件：下载完整后才改名，保证「正式文件存在 = 完整」
            lock (_dlLock)
            {
                try
                {
                    if (!(WebRequest.Create(Url) is HttpWebRequest request) || request == null)
                        return false;

                    using (HttpWebResponse response = request.GetResponse() as HttpWebResponse)
                    {
                        if (response == null) return false;
                        using (Stream responseStream = response.GetResponseStream())
                        using (FileStream fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            byte[] bArr = new byte[1024];
                            int size = responseStream.Read(bArr, 0, bArr.Length);
                            while (size > 0)
                            {
                                fs.Write(bArr, 0, size);
                                size = responseStream.Read(bArr, 0, bArr.Length);
                            }
                        }
                    }

                    // 下载完整（>0 字节）后才原子重命名，避免残留残缺文件被当作完整文件播放
                    if (System.IO.File.Exists(tmpPath) && new FileInfo(tmpPath).Length > 0)
                    {
                        if (System.IO.File.Exists(finalPath)) { try { System.IO.File.Delete(finalPath); } catch { } }
                        System.IO.File.Move(tmpPath, finalPath);
                        return true;
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"HttpDownload() Error Message:{ex.Message}");
                    try { if (System.IO.File.Exists(tmpPath)) System.IO.File.Delete(tmpPath); } catch { }
                    return false;
                }
            }
        }

        public static bool PlayMp3(object Speech)
        {
            bool ret = true;
            List<string> TempSpeech = (List<string>)Speech;
            string mp3_full_name = ExeDir + @"\Mp3Cache\" + TempSpeech[0] + ".mp3";
            // 文件存在但可能残缺（历史下载中断残留）→ 大小过小视为不完整，删除后重新下载
            if (File.Exists(mp3_full_name))
            {
                try
                {
                    FileInfo fi = new FileInfo(mp3_full_name);
                    if (fi.Length < 1024) File.Delete(mp3_full_name);   // 残缺文件，删除走重新下载
                }
                catch { }
            }
            if (!File.Exists(mp3_full_name))
            {
                DownloadMp3 Download = new DownloadMp3();
                bool flag = Download.HttpDownload("https://dict.youdao.com/dictvoice?audio=" + TempSpeech[1], TempSpeech[0]);
                if (flag != true) ret = false;
            }
            if (ret) ret = PlayOriginalWithLeadSilence(mp3_full_name);
            return ret;
        }

        /// <summary>
        /// 播放单词音频：MP3 走 MP3Sharp 解码、WAV 直接解析，统一构建 WAV（前置 1 秒空音）后 SoundPlayer 播放。
        /// MP3Sharp 输出采样率：mono 是 Frequency×2（每采样重复一次）、stereo 是 Frequency×1（实测 MPEG1 mono ratio 精确 2.0、stereo 精确 1.0）。
        /// 有道对部分单词返回 WAV（扩展名仍 .mp3），由 IsWavFile 识别后直接读 fmt 采样率。
        /// </summary>
        private static bool PlayOriginalWithLeadSilence(string path)
        {
            try
            {
                byte[] pcm;
                int freq, ch;
                if (IsWavFile(path))
                {
                    // WAV：fmt chunk 里的采样率就是正确值
                    ParseWav(path, out freq, out ch, out pcm);
                }
                else
                {
                    using (var mp3 = new MP3Sharp.MP3Stream(path))
                    {
                        ch = mp3.ChannelCount;
                        freq = mp3.Frequency;
                        using (var ms = new MemoryStream())
                        {
                            byte[] buf = new byte[4096];
                            int n;
                            while ((n = mp3.Read(buf, 0, buf.Length)) > 0)
                                ms.Write(buf, 0, n);
                            pcm = ms.ToArray();
                        }
                    }
                    // MP3Sharp 对 mono 输出采样率 ×2、stereo ×1
                    freq = (ch == 1) ? freq * 2 : freq;
                }
                if (pcm == null || pcm.Length < 44) return false;

                // 前置 1 秒空音：规避 USB DAC 启动吞开头约 0.5 秒音频的问题
                int leadBytes = freq * ch * 2;
                byte[] wav = BuildWav(freq, ch, pcm, 0, pcm.Length, leadBytes);

                using (var stream = new MemoryStream(wav))
                using (var player = new System.Media.SoundPlayer(stream))
                {
                    player.PlaySync();
                }
                return true;
            }
            catch { }
            return false;
        }

        /// <summary>判断文件是否为 WAV（RIFF....WAVE 头）。有道对部分单词返回 WAV 却存成 .mp3 扩展名。</summary>
        private static bool IsWavFile(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] head = new byte[12];
                    if (fs.Read(head, 0, head.Length) < head.Length) return false;
                    return head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
                        && head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E';
                }
            }
            catch { return false; }
        }

        /// <summary>解析 WAV 的 fmt/data chunk，取出 16-bit PCM 数据、采样率、声道。</summary>
        private static void ParseWav(string path, out int freq, out int ch, out byte[] pcm)
        {
            byte[] raw = File.ReadAllBytes(path);
            freq = 0; ch = 0; pcm = null;
            int dataOffset = -1, dataLen = 0;
            int pos = 12;
            while (pos + 8 <= raw.Length)
            {
                string id = Encoding.ASCII.GetString(raw, pos, 4);
                int size = BitConverter.ToInt32(raw, pos + 4);
                if (id == "fmt ")
                {
                    ch = BitConverter.ToInt16(raw, pos + 10);
                    freq = BitConverter.ToInt32(raw, pos + 12);
                }
                else if (id == "data")
                {
                    dataOffset = pos + 8;
                    dataLen = size;
                    break;
                }
                pos += 8 + size + (size & 1);
            }
            if (dataOffset < 0 || dataLen <= 0 || freq <= 0 || ch <= 0) return;
            int copy = Math.Min(dataLen, raw.Length - dataOffset);
            pcm = new byte[copy];
            Array.Copy(raw, dataOffset, pcm, 0, copy);
        }

        /// <summary>构造标准 44 字节 WAV 头 + 16-bit PCM 数据（signed little-endian，采样率=freq）。</summary>
        private static byte[] BuildWav(int freq, int ch, byte[] pcm, int offset, int dataLen, int leadBytes)
        {
            int headerSize = 44;
            int totalData = leadBytes + dataLen;
            byte[] wav = new byte[headerSize + totalData];
            int byteRate = freq * ch * 2;
            int blockAlign = ch * 2;

            wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
            BitConverter.GetBytes(wav.Length - 8).CopyTo(wav, 4);
            wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
            wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
            BitConverter.GetBytes(16).CopyTo(wav, 16);
            BitConverter.GetBytes((short)1).CopyTo(wav, 20);      // PCM
            BitConverter.GetBytes((short)ch).CopyTo(wav, 22);
            BitConverter.GetBytes(freq).CopyTo(wav, 24);
            BitConverter.GetBytes(byteRate).CopyTo(wav, 28);
            BitConverter.GetBytes((short)blockAlign).CopyTo(wav, 32);
            BitConverter.GetBytes((short)16).CopyTo(wav, 34);     // 16-bit
            wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
            BitConverter.GetBytes(totalData).CopyTo(wav, 40);

            // 前置空音（leadBytes 字节，默认 0 即静音），发音 PCM 跟在空音之后
            Array.Copy(pcm, offset, wav, headerSize + leadBytes, dataLen);
            return wav;
        }
    } 
}
