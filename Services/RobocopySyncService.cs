using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Offloader.Services
{
    public class RobocopyResult
    {
        public int ExitCode { get; set; }
        public bool Success => ExitCode < 8;
        public string Output { get; set; } = string.Empty;
    }

    /// <summary>
    /// robocopy 封装。成功判定：ExitCode &lt; 8（0-7 为成功，含“复制了文件”）。
    /// 推送永远不删远端（不使用 /MIR），与 ps1 脚本行为一致。
    /// 输出采用异步流式读取：一是避免 stdout/stderr 缓冲区写满导致子进程挂起（前台永远加载的根因），
    /// 二是实时解析出当前文件/%/速度刷到前台进度文本。
    /// </summary>
    public class RobocopySyncService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly string RobocopyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "robocopy.exe");
        private const int OutputCap = 65536;

        // robocopy 重定向输出的真实格式（已用真机实测，中文 locale）：
        //   文件起始行：`新文件 \t 5.0 m \t C:\...\file.pak`（状态词 + 大小 + 路径，无 %）
        //   进度行：光秃秃的 `  0%` / `100%`（无文件名，需与上一文件行配对）
        //   结尾汇总：`速度 : 6,672,756,363 字节/秒。`（传完才有，仅收尾展示）
        private static readonly Regex BarePctRegex = new Regex(@"^\s*(\d+(?:\.\d+)?)%\s*$", RegexOptions.Compiled);
        private static readonly Regex NewFileRegex = new Regex(@"^\s*(?:新文件|新目录|New File|New Dir|Newer|Older|Changed|Modified|Larger)\s+(.*?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SizePrefixRegex = new Regex(@"^[\d,\.]+\s*[kKmMgG]?\s+", RegexOptions.Compiled);
        // 结尾汇总行：`  速度 :   53477333 字节/秒。` / `  Speed : ...`
        private static readonly Regex SpeedLineRegex = new Regex(@"(?:速度|Speed)\s*[:：]\s*([\d,\.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public RobocopyResult Pull(string remotePath, string localPath, CancellationToken cancelToken, string extraArgs, Action<string> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                throw new ArgumentException(ResourceProvider.GetString("LOCoffloaderErrArgRemoteEmpty"));
            }
            if (string.IsNullOrWhiteSpace(localPath))
            {
                throw new ArgumentException(ResourceProvider.GetString("LOCoffloaderErrArgLocalEmpty"));
            }
            if (!Directory.Exists(remotePath))
            {
                throw new DirectoryNotFoundException(string.Format(ResourceProvider.GetString("LOCoffloaderErrRemoteMissing"), remotePath));
            }
            Directory.CreateDirectory(localPath);
            // 拉取恒为前台多线程全速，仅进程优先级让行（BelowNormal 让 CPU，不降吞吐）
            return Run(remotePath, localPath, extraArgs, cancelToken, true, onProgress);
        }

        public RobocopyResult Push(string localPath, string remotePath, CancellationToken cancelToken, string extraArgs, bool lowPriority, Action<string> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(localPath))
            {
                throw new ArgumentException(ResourceProvider.GetString("LOCoffloaderErrArgLocalEmpty"));
            }
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                throw new ArgumentException(ResourceProvider.GetString("LOCoffloaderErrArgRemoteEmpty"));
            }
            if (!Directory.Exists(localPath))
            {
                throw new DirectoryNotFoundException(string.Format(ResourceProvider.GetString("LOCoffloaderErrLocalDirMissing"), localPath));
            }
            Directory.CreateDirectory(remotePath);
            return Run(localPath, remotePath, extraArgs, cancelToken, lowPriority, onProgress);
        }

        public bool RemoteHasData(string remotePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(remotePath) || !Directory.Exists(remotePath))
                {
                    return false;
                }
                return Directory.EnumerateFileSystemEntries(remotePath).Any();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Offloader: 检查远端数据失败：" + remotePath);
                return false;
            }
        }

        /// <summary>
        /// 是否已启用：远端存在 GameId 目录即算（空目录也算，靠下次推送自愈）。
        /// 与 <see cref="RemoteHasData"/>（非空才算有数据）区分：前者管菜单/自动推门控，后者管释放校验/安装恢复。
        /// </summary>
        public bool RemoteExists(string remotePath)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(remotePath) && Directory.Exists(remotePath);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Offloader: 检查远端目录失败：" + remotePath);
                return false;
            }
        }

        /// <summary>
        /// 统计目录内文件数与总字节数（本地/远端通用）。失败返回 false，调用方显示兜底文案仍可继续。
        /// 用于释放确认框的本地↔远端比对展示、恢复空间预检。
        /// </summary>
        public bool TryGetTreeStats(string path, out long fileCount, out long totalBytes)
        {
            fileCount = 0;
            totalBytes = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    return false;
                }
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        fileCount++;
                        totalBytes += new FileInfo(file).Length;
                    }
                    catch
                    {
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Offloader: 统计目录数据失败：" + path);
                return false;
            }
        }

        /// <summary>
        /// 构建 相对路径→文件大小 映射（本地/远端通用，忽略大小写）。失败返回 false。
        /// 供释放前逐文件校验：远端必须包含本地每个文件且大小一致；远端多余文件放行
        /// （与「永不删除远端」约定一致，归档天然积累 extras）。
        /// </summary>
        public bool TryGetFileMap(string path, out Dictionary<string, long> fileMap)
        {
            fileMap = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    return false;
                }
                var root = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        map[file.Substring(root.Length)] = new FileInfo(file).Length;
                    }
                    catch
                    {
                    }
                }
                fileMap = map;
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Offloader: 构建文件映射失败：" + path);
                return false;
            }
        }

        /// <summary>
        /// 统计远端文件数与总字节数（供恢复确认框展示）。委托 <see cref="TryGetTreeStats"/>。
        /// </summary>
        public bool TryGetRemoteStats(string remotePath, out long fileCount, out long totalBytes)
        {
            return TryGetTreeStats(remotePath, out fileCount, out totalBytes);
        }

        /// <summary>
        /// 查询目标路径所在驱动器的剩余可用字节数。仅支持本地盘/映射盘符；
        /// UNC 共享（\server\share）DriveInfo 不支持，返回 false（调用方据此放行预检）。
        /// </summary>
        public bool TryGetFreeBytes(string path, out long freeBytes)
        {
            freeBytes = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return false;
                }
                var di = new DriveInfo(root);
                if (!di.IsReady)
                {
                    return false;
                }
                freeBytes = di.AvailableFreeSpace;
                return true;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Offloader: 查询可用空间失败：" + path);
                return false;
            }
        }

        private RobocopyResult Run(string source, string dest, string extraArgs, CancellationToken cancelToken, bool lowPriority, Action<string> onProgress)
        {
            var args = string.Format("\"{0}\" \"{1}\" {2}", source.TrimEnd('\\'), dest.TrimEnd('\\'), extraArgs ?? string.Empty);
            logger.Info(string.Format("Offloader: robocopy {0}", args));
            onProgress?.Invoke(string.Format("{0}\n{1}\n→ {2}", ResourceProvider.GetString("LOCoffloaderSyncSyncing"), source, dest));

            var psi = new ProcessStartInfo
            {
                FileName = RobocopyPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var outputLock = new object();
                var output = new StringBuilder(4096);
                var lastReport = DateTime.UtcNow;
                string currentFile = null;
                string lastPct = null;

                DataReceivedEventHandler onOut = (s, e) =>
                {
                    var line = e.Data;
                    if (line == null)
                    {
                        return;
                    }
                    lock (outputLock)
                    {
                        if (output.Length < OutputCap)
                        {
                            output.AppendLine(line);
                        }
                    }
                    if (onProgress == null)
                    {
                        return;
                    }
                    try
                    {
                        var trimmed = line.Trim().TrimEnd('\r');
                        if (trimmed.Length == 0)
                        {
                            return;
                        }
                        var speedMatch = SpeedLineRegex.Match(trimmed);
                        if (speedMatch.Success && double.TryParse(speedMatch.Groups[1].Value.Replace(",", string.Empty), out var bytesPerSec) && bytesPerSec > 0)
                        {
                            onProgress(string.Format("{0}\n{1}\n→ {2}", string.Format(ResourceProvider.GetString("LOCoffloaderSyncAvgSpeed"), bytesPerSec / 1048576.0), source, dest));
                            return;
                        }
                        var fileMatch = NewFileRegex.Match(trimmed);
                        if (fileMatch.Success)
                        {
                            var rest = SizePrefixRegex.Replace(fileMatch.Groups[1].Value.Trim(), string.Empty).Trim();
                            try
                            {
                                var name = Path.GetFileName(rest);
                                if (!string.IsNullOrEmpty(name))
                                {
                                    rest = name;
                                }
                            }
                            catch
                            {
                            }
                            if (rest.Length > 60)
                            {
                                rest = "…" + rest.Substring(rest.Length - 59);
                            }
                            currentFile = rest;
                            lastPct = null;
                            lastReport = DateTime.UtcNow;
                            onProgress(string.Format("{0}\n{1}\n→ {2}", currentFile, source, dest));
                            return;
                        }
                        var pctMatch = BarePctRegex.Match(trimmed);
                        if (pctMatch.Success)
                        {
                            lastPct = pctMatch.Groups[1].Value;
                            var now = DateTime.UtcNow;
                            var isDone = lastPct.StartsWith("100");
                            if (!isDone && (now - lastReport).TotalMilliseconds < 500)
                            {
                                return;
                            }
                            lastReport = now;
                            var what = string.IsNullOrEmpty(currentFile) ? lastPct + "%" : lastPct + "% · " + currentFile;
                            onProgress(string.Format("{0}\n{1}\n→ {2}", what, source, dest));
                        }
                    }
                    catch
                    {
                    }
                };

                DataReceivedEventHandler onErr = (s, e) =>
                {
                    if (e.Data == null)
                    {
                        return;
                    }
                    lock (outputLock)
                    {
                        if (output.Length < OutputCap)
                        {
                            output.AppendLine(e.Data);
                        }
                    }
                };

                try
                {
                    proc.Start();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(string.Format(ResourceProvider.GetString("LOCoffloaderErrRobocopyStart"), ex.Message), ex);
                }

                try
                {
                    if (lowPriority)
                    {
                        try
                        {
                            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                        }
                        catch (Exception ex)
                        {
                            logger.Warn("Offloader: 设置低优先级失败：" + ex.Message);
                        }
                    }

                    proc.OutputDataReceived += onOut;
                    proc.ErrorDataReceived += onErr;
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    // 等待退出，同时响应取消；输出已由事件持续排空，不会再因缓冲区写满挂起
                    while (!proc.WaitForExit(500))
                    {
                        if (cancelToken.IsCancellationRequested)
                        {
                            try
                            {
                                proc.Kill();
                            }
                            catch
                            {
                            }
                            cancelToken.ThrowIfCancellationRequested();
                        }
                    }
                    // 等待异步输出排空
                    proc.WaitForExit();

                    string full;
                    lock (outputLock)
                    {
                        full = output.ToString();
                    }
                    var result = new RobocopyResult { ExitCode = proc.ExitCode, Output = full };
                    logger.Info(string.Format("Offloader: robocopy 退出码 {0}（<8 成功）", proc.ExitCode));
                    return result;
                }
                finally
                {
                    try
                    {
                        proc.CancelOutputRead();
                    }
                    catch
                    {
                    }
                    try
                    {
                        proc.CancelErrorRead();
                    }
                    catch
                    {
                    }
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill();
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
