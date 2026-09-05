using Playnite.SDK;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// </summary>
    public class RobocopySyncService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly string RobocopyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "robocopy.exe");

        public string PullArgs { get; set; } = "/E /R:2 /W:5 /MT:16";
        public string PushArgs { get; set; } = "/E /XO /IPG:50 /R:1 /W:3 /NP /NDL";

        public RobocopyResult Pull(string remotePath, string localPath, CancellationToken cancelToken, Action<string> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                throw new ArgumentException("远端路径为空。");
            }
            if (string.IsNullOrWhiteSpace(localPath))
            {
                throw new ArgumentException("本地路径为空。");
            }
            if (!Directory.Exists(remotePath))
            {
                throw new DirectoryNotFoundException("远端仓库不存在：" + remotePath);
            }
            Directory.CreateDirectory(localPath);
            // 拉取用多线程加速，正常优先级（阻塞式恢复，用户在等）
            return Run(remotePath, localPath, PullArgs, cancelToken, false, onProgress);
        }

        public RobocopyResult Push(string localPath, string remotePath, CancellationToken cancelToken, bool lowPriority, Action<string> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(localPath))
            {
                throw new ArgumentException("本地路径为空。");
            }
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                throw new ArgumentException("远端路径为空。");
            }
            if (!Directory.Exists(localPath))
            {
                throw new DirectoryNotFoundException("本地游戏目录不存在：" + localPath);
            }
            Directory.CreateDirectory(remotePath);
            return Run(localPath, remotePath, PushArgs, cancelToken, lowPriority, onProgress);
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

        private RobocopyResult Run(string source, string dest, string extraArgs, CancellationToken cancelToken, bool lowPriority, Action<string> onProgress)
        {
            var args = string.Format("\"{0}\" \"{1}\" {2}", source.TrimEnd('\\'), dest.TrimEnd('\\'), extraArgs ?? string.Empty);
            logger.Info(string.Format("Offloader: robocopy {0}", args));
            onProgress?.Invoke(string.Format("正在同步…\n{0}\n→ {1}", source, dest));

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
                var output = string.Empty;
                try
                {
                    proc.Start();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("无法启动 robocopy：" + ex.Message, ex);
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

                    // 等待退出，同时响应取消
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

                    try
                    {
                        output = proc.StandardOutput.ReadToEnd();
                    }
                    catch
                    {
                    }

                    var result = new RobocopyResult { ExitCode = proc.ExitCode, Output = output };
                    logger.Info(string.Format("Offloader: robocopy 退出码 {0}（<8 成功）", proc.ExitCode));
                    return result;
                }
                finally
                {
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
