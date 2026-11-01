using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace GeLiLauncher.Core
{
    /// <summary>一个下载任务（安装版本、下载模组、导入整合包都走这里）</summary>
    public class DownloadJob : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Title { get; set; }

        string _detail = "准备中…";
        public string Detail
        {
            get => _detail;
            set { _detail = value; OnChanged(nameof(Detail)); }
        }

        double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = Math.Max(0, Math.Min(1, value)); OnChanged(nameof(Progress)); OnChanged(nameof(PercentText)); }
        }

        public string PercentText => (int)Math.Round(Progress * 100) + "%";

        string _status = "running";
        public string Status
        {
            get => _status;
            set { _status = value; OnChanged(nameof(Status)); OnChanged(nameof(IsRunning)); OnChanged(nameof(StatusText)); }
        }

        public bool IsRunning => _status == "running";

        public string StatusText => _status switch
        {
            "done" => "已完成",
            "failed" => "失败",
            "cancelled" => "已取消",
            _ => "进行中"
        };

        public DateTime StartedAt { get; set; } = DateTime.Now;
        public string Error { get; set; }

        internal CancellationTokenSource Cts { get; set; } = new CancellationTokenSource();

        public event PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string name) =>
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name))));
    }

    /// <summary>下载任务中心：所有耗时下载统一登记，界面可以看进度、取消</summary>
    public static class DownloadCenter
    {
        public static ObservableCollection<DownloadJob> Jobs { get; } = new ObservableCollection<DownloadJob>();

        /// <summary>启动一个任务；work 里用 report(进度, 说明) 回报进度</summary>
        public static DownloadJob Start(string title, Func<Action<double, string>, CancellationToken, Task> work)
        {
            var job = new DownloadJob { Id = Gll.NewId("job"), Title = title };
            Add(job);

            _ = Task.Run(async () =>
            {
                try
                {
                    await work((p, detail) =>
                    {
                        job.Progress = p;
                        if (!string.IsNullOrEmpty(detail)) job.Detail = detail;
                    }, job.Cts.Token);

                    job.Progress = 1;
                    job.Detail = "完成";
                    job.Status = "done";
                }
                catch (OperationCanceledException)
                {
                    job.Status = "cancelled";
                    job.Detail = "已取消";
                }
                catch (Exception ex)
                {
                    job.Status = "failed";
                    job.Error = ex.Message;
                    job.Detail = ex.Message;
                    Diag.Write(ex, "下载任务：" + title);
                }
            });

            return job;
        }

        /// <summary>登记一个已有 UI 在跑的任务（页面自带进度条时用），返回可更新的 job</summary>
        public static DownloadJob Attach(string title)
        {
            var job = new DownloadJob { Id = Gll.NewId("job"), Title = title };
            Add(job);
            return job;
        }

        public static void Update(DownloadJob job, double progress, string detail = null)
        {
            if (job == null) return;
            job.Progress = progress;
            if (!string.IsNullOrEmpty(detail)) job.Detail = detail;
        }

        public static void Finish(DownloadJob job, bool ok, string error = null)
        {
            if (job == null) return;
            job.Status = ok ? "done" : "failed";
            if (ok) { job.Progress = 1; job.Detail = "完成"; }
            else { job.Error = error; job.Detail = error ?? "失败"; }
        }

        static void Add(DownloadJob job) =>
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Jobs.Insert(0, job)));

        public static void Cancel(DownloadJob job)
        {
            try { job.Cts.Cancel(); } catch { }
        }

        /// <summary>清掉已经结束的任务</summary>
        public static void ClearFinished()
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                for (var i = Jobs.Count - 1; i >= 0; i--)
                    if (!Jobs[i].IsRunning) Jobs.RemoveAt(i);
            }));
        }

        public static int RunningCount
        {
            get
            {
                var n = 0;
                foreach (var j in Jobs) if (j.IsRunning) n++;
                return n;
            }
        }
    }
}
