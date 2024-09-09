using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;


namespace ResourceManagerUpdater
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private int progress;
        public int Progress
        {
            get { return progress; }
            set
            {
                progress = value;
                NotifyPropertyChanged();
            }
        }

        private long downloadedSize;
        public long DownloadedSize
        {
            get { return downloadedSize; }
            set
            {
                downloadedSize = value;
                NotifyPropertyChanged();
            }
        }

        private long updatesSize;
        public long UpdatesSize
        {
            get { return updatesSize; }
            set
            {
                updatesSize = value;
                NotifyPropertyChanged();
            }
        }

        private string updateName;
        public string UpdateName
        {
            get { return updateName; }
            set
            {
                updateName = value;
                NotifyPropertyChanged();
            }
        }

        private string progressText;
        public string ProgressText
        {
            get { return progressText; }
            set
            {
                progressText = value;
                NotifyPropertyChanged();
            }
        }

        private string availableVersion;
        public string AvailableVersion
        {
            get { return availableVersion; }
            set
            {
                availableVersion = value;
                NotifyPropertyChanged();
            }
        }

        public class Update
        {
            public string name { get; set; }
            public string url { get; set; }
            public string md5 { get; set; }
            public long size { get; set; }
            public string install_path { get; set; }

        }
        public string CurrentVersion
        {
            get
            {
                return "0.7.0";
            }
        } 
        public string AvailableVersionUrl
        {
            get
            {
                return "https://github.com/VladTheJunior/Resource-Manager";
            }
        } 

        public class Updates
        {
            public string version { get; set; }
            public string url { get; set; }
            public List<Update> files { get; set; } = new List<Update>();
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            var psi = new ProcessStartInfo
            {
                FileName = e.Uri.ToString(),
                UseShellExecute = true
            };
            Process.Start(psi);
        }


        public Updates ServerUpdates { get; set; } = new Updates();
        public Updates ClientUpdates { get; set; } = new Updates();
        public ConcurrentQueue<Update> NewUpdates { get; set; } = new ConcurrentQueue<Update>();

        private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromMinutes(60) };

        static async Task<string> CalculateMD5(string filename)
        {
            using var file = File.OpenRead(filename);
            byte[] hash = await new K4os.Hash.xxHash.XXH32().AsHashAlgorithm().ComputeHashAsync(file);
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        private async Task<string> HttpGetAsync(string URI)
        {
            try
            {
                HttpClient hc = new HttpClient();
                Task<System.IO.Stream> result = hc.GetStreamAsync(URI);

                System.IO.Stream vs = await result;
                using (StreamReader am = new StreamReader(vs, Encoding.UTF8))
                {
                    return await am.ReadToEndAsync();
                }
            }
            catch
            {
                return "error";
            }
        }

        private async Task<bool> DownloadFile(Update update)
        {
            try
            {


                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(AppContext.BaseDirectory, update.install_path)));

                HttpResponseMessage response = await httpClient.GetAsync(update.url);
                // Check that response was successful or throw exception
                response.EnsureSuccessStatusCode();

                // Read response asynchronously and save asynchronously to file
                using (FileStream fileStream = new FileStream(Path.Combine(AppContext.BaseDirectory, update.install_path), FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    //copy the content from response to filestream
                    await response.Content.CopyToAsync(fileStream);


                }

                Interlocked.Increment(ref progress);
                NotifyPropertyChanged("Progress");
                ProgressText = $"Downloaded: file {progress} of {maximum}";
                UpdateName = update.install_path;
            }
            catch
            {

            }
            return true;

         
        }

        async Task<Updates> CheckUpdates()
        {
            string json = await HttpGetAsync("https://raw.githubusercontent.com/VladTheJunior/ResourceManagerUpdates/master/Updates.json");
            Updates res = new ();
            try
            {
                res = JsonSerializer.Deserialize<Updates>(json);
            }
            catch
            {
            }
            return res;
        }

        public MainWindow()
        {
            ServicePointManager.UseNagleAlgorithm = false;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = int.MaxValue;
            ServicePointManager.EnableDnsRoundRobin = true;
            ServicePointManager.ReusePort = true;
            InitializeComponent();
            DataContext = this;
        }

        private object CheckLock = new object();

        private int maximum = 0;

        private async Task<bool> ScanFile(string path)
        {
            var hash = await CalculateMD5(path);
            lock (CheckLock)
            {
                ClientUpdates.files.Add(new Update { name = Path.GetFileName(path), size = new FileInfo(path).Length, install_path = path.Remove(0, AppContext.BaseDirectory.Length), md5 = hash, url = new Uri(new Uri("https://raw.githubusercontent.com/VladTheJunior/ResourceManagerUpdates/master/"), path.Remove(0, AppContext.BaseDirectory.Length)).ToString() });
            }
            Interlocked.Increment(ref progress);
            NotifyPropertyChanged("Progress");
            ProgressText = $"Checked: file {progress} of {maximum}";
            UpdateName = path.Remove(0, AppContext.BaseDirectory.Length);
            return true;
        }



        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await Task.Delay(1);
            AvailableVersion = "checking...";
            int index = 1;
            ConcurrentQueue<string> updateFiles = new(Directory.GetFiles(AppContext.BaseDirectory, "*.*", SearchOption.AllDirectories).Where(path => !path.Contains(".git")
                    && Path.GetFileName(path) != "UpdateCounter.txt" && Path.GetFileName(path) != "Updates.json" && !path.Contains("Cached")
                    && Path.GetFileName(path) != ".gitignore" && Path.GetFileName(path) != ".gitattributes" && Path.GetFileName(Path.GetDirectoryName(path)) != "Output"));

            pbProgress.Maximum = updateFiles.Count();


            maximum = updateFiles.Count();

            var CheckTasks = new List<Task>();
            for (int i = 0; i < 300; i++)
            {
                CheckTasks.Add(Task.Run(async () =>
                {
                    while (updateFiles.TryDequeue(out string update))
                    {
                        await ScanFile(update);
                    }
                }
                ));
            }

            await Task.WhenAll(CheckTasks);
            ClientUpdates.version = CurrentVersion;
            ClientUpdates.url = AvailableVersionUrl;
            await File.WriteAllTextAsync("Updates.json", JsonSerializer.Serialize(ClientUpdates));

            /*    */
            ServerUpdates = await CheckUpdates();

            
            if (ServerUpdates.files.Count > 0)
            {
                var differences = ServerUpdates.files.Where(s => !ClientUpdates.files.Any(c => c.install_path == s.install_path && c.md5 == s.md5));
                foreach (Update f in differences)
                {
                    if (f.name == "ResourceManagerUpdater.exe" || f.name == "ResourceManagerUpdater.dll")
                        f.install_path += ".upd";
                    NewUpdates.Enqueue(f);
                }
            }
            if (NewUpdates.Count > 0)
            {
                AvailableVersion = ServerUpdates.version;
                foreach (var process in Process.GetProcessesByName("Resource Manager"))
                {
                    process.Kill();
                }
            }
            else
            {
                AvailableVersion = "up-to-dated";
            }
            Progress = 0;
            pbProgress.Maximum = NewUpdates.Count;
            maximum = NewUpdates.Count();

            var UpdateTasks = new List<Task>();
            for (int i = 0; i < 20; i++)
            {
                UpdateTasks.Add(Task.Run(async () =>
                {
                    while (NewUpdates.TryDequeue(out Update update))
                    {
                        await DownloadFile(update);
                    }
                }
                ));
            }

            await Task.WhenAll(UpdateTasks);

            using (var batFile = new StreamWriter(File.Create("Update.bat")))
            {
                string file = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName);
                batFile.WriteLine("@ECHO OFF");
                batFile.WriteLine("TIMEOUT /t 1 /nobreak > NUL");
                batFile.WriteLine("TASKKILL /F /IM \"{0}\" > NUL", file);
                batFile.WriteLine("IF EXIST \"{0}\" MOVE \"{0}\" \"{1}\"", file + ".upd", file);
                batFile.WriteLine("IF EXIST \"{0}\" MOVE \"{0}\" \"{1}\"", Path.GetFileNameWithoutExtension(file) + ".dll.upd", Path.GetFileNameWithoutExtension(file) + ".dll");
                batFile.WriteLine("DEL \"%~f0\" & START \"\" /B \"{0}\"", "Resource Manager.exe");
            }
            ProcessStartInfo startInfo = new ProcessStartInfo("Update.bat");
            startInfo.CreateNoWindow = true;
            startInfo.UseShellExecute = false;
            startInfo.WorkingDirectory = AppContext.BaseDirectory;
            Process.Start(startInfo);
            Environment.Exit(0);

        }
    }
}
