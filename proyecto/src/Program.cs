using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EmpireBay
{
    static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool probe = args.Length == 1 && args[0] == "--comprobar";
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var install = new Install(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmpireBayRP"));
                if (!probe && !Install.Same(Install.Self, install.FileFor(Install.Current)))
                {
                    if (!Install.Same(Install.Self, install.Entry))
                    {
                        install.Commit(Install.Self, Install.Current);
                        install.Shortcut();
                    }
                    using (Install.Start(install.LatestLocal(), "")) { }
                    return 0;
                }
                using (var instance = new Mutex(false, Install.LocalName("window")))
                {
                    if (!probe && !Install.Wait(instance, args.Length == 1 && args[0] == "--actualizado" ? 12000 : 0))
                        return 0;
                    try
                    {
                        var app = new Application();
                        var launcher = new LauncherWindow(install, Distribution.Online());
                        if (probe)
                        {
                            launcher.View.Measure(new Size(880, 607));
                            launcher.View.Arrange(new Rect(0, 0, 880, 607));
                            launcher.View.UpdateLayout();
                            return 0;
                        }
                        launcher.Start();
                        return app.Run(launcher.View);
                    }
                    finally { if (!probe) instance.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                if (!probe) MessageBox.Show(ex.Message, "Empire Bay Roleplay", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    public sealed class LauncherWindow
    {
        public readonly Window View;
        readonly Install install;
        readonly Distribution downloads;
        readonly GameClient game;
        readonly CancellationTokenSource closing = new CancellationTokenSource();
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        readonly Settings defaults;
        Settings settings;
        bool busy;
        bool installing;
        bool closed;
        bool restart;
        readonly Button play, client, guide, retry;
        readonly TextBlock info, state, status;
        readonly ProgressBar progress;

        T Element<T>(string name) where T : class { return (T)View.FindName(name); }

        static object Markup(string name)
        {
            using (var stream = Settings.Resource(name)) return XamlReader.Load(stream);
        }

        public LauncherWindow(Install install, Distribution downloads)
        {
            this.install = install;
            this.downloads = downloads;
            game = new GameClient(install, downloads);
            defaults = Settings.Read();
            View = (Window)Markup("Window.xaml");
            using (var icon = Settings.Resource("EmpireBay.ico"))
                View.Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Element<Image>("Brand").Source = Scene.ReadImage("logo.png");
            var gallery = Element<ListBox>("Gallery");
            gallery.ItemsSource = Scene.Album();
            gallery.SelectionChanged += (s,e) => {
                var scene = gallery.SelectedItem as Scene;
                if (scene == null) return;
                var hero = Element<Image>("HeroImage");
                bool change = hero.Source != null;
                hero.Source = scene.Picture;
                AutomationProperties.SetName(hero, scene.Caption);
                Element<TextBlock>("SceneCaption").Text = scene.Caption;
                if (change && SystemParameters.ClientAreaAnimation)
                    hero.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.7, 1, TimeSpan.FromMilliseconds(170)) { FillBehavior = FillBehavior.Stop });
            };
            gallery.SelectedIndex = 0;
            Element<Button>("Minimize").Click += (s,e) => View.WindowState = WindowState.Minimized;
            Element<Button>("Maximize").Click += (s,e) => View.WindowState = View.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            Element<Button>("CloseApp").Click += (s,e) => View.Close();
            View.StateChanged += (s,e) => {
                Element<System.Windows.Shapes.Path>("MaximizeIcon").Data = Geometry.Parse(View.WindowState == WindowState.Maximized
                    ? "M3,1 L11,1 11,9 9,9 M1,3 L9,3 9,11 1,11 Z" : "M1,1 L10,1 10,10 1,10 Z");
            };
            play = Element<Button>("Play"); client = Element<Button>("Client");
            guide = Element<Button>("Guide"); retry = Element<Button>("Retry");
            state = Element<TextBlock>("ServerState"); info = Element<TextBlock>("Info");
            status = Element<TextBlock>("Status"); progress = Element<ProgressBar>("Progress");
            Element<TextBlock>("VersionText").Text = "Launcher  v" + Install.Current.ToString(3);
            Element<Button>("Web").Click += (s,e) => OpenWeb(Settings.Website);
            guide.Click += (s,e) => OpenWeb(settings.guideUrl);
            play.Click += (s,e) => { try { game.Play(settings, View); } catch (Exception ex) { Message(ex.Message); } };
            retry.Click += async (s,e) => await Check();
            client.Click += async (s,e) => { if (settings.HasClient) await InstallClient(); else OpenWeb(settings.guideUrl); };
            Apply(defaults);
        }

        public void Start()
        {
            View.ContentRendered += OnFirstFrame;
            timer.Tick += async (s,e) => await Check();
            View.Closing += (s,e) =>
            {
                if (installing) { e.Cancel = true; Message("Esperá a que termine la instalación del cliente."); return; }
                closed = true;
                timer.Stop();
                closing.Cancel();
            };
        }

        async void OnFirstFrame(object sender, EventArgs args)
        {
            View.ContentRendered -= OnFirstFrame;
            timer.Start();
            await Check();
        }

        void Apply(Settings value)
        {
            settings = value;
            play.IsEnabled = value.playEnabled && !busy;
            play.ToolTip = value.playEnabled ? value.host + ":" + value.port : "El servidor está en mantenimiento.";
            state.Text = value.playEnabled ? "Conexión habilitada" : "En mantenimiento";
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value.playEnabled ? "#90D8B0" : "#E8C36E");
            state.Foreground = brush;
            Element<System.Windows.Shapes.Ellipse>("Light").Fill = brush;
            client.Content = value.HasClient ? "Descargar e instalar" : "Abrir descarga en Discord  ↗";
            guide.Visibility = value.HasClient ? Visibility.Visible : Visibility.Collapsed;
            info.Text = value.HasClient ? "Instalá el cliente multijugador de Mafia II." : "Encontrá el cliente y la guía en #⬇️・descargar.";
        }

        void Message(string text) { if (!closed) status.Text = text; }
        void OpenWeb(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Message(ex.Message); }
        }

        void SetBusy(bool active)
        {
            busy = active;
            retry.IsEnabled = !active;
            client.IsEnabled = !active || !settings.HasClient;
            play.IsEnabled = !active && settings.playEnabled;
            progress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            progress.IsIndeterminate = active;
        }

        void Percent(int percent)
        {
            View.Dispatcher.BeginInvoke(new Action(() => {
                if (!busy || closed) return;
                progress.IsIndeterminate = percent < 0;
                if (percent >= 0) progress.Value = percent;
            }));
        }

        async Task Check()
        {
            if (busy || closed) return;
            string temp = null;
            SetBusy(true);
            Message("Buscando actualizaciones…");
            try
            {
                var token = closing.Token;
                Release release = await downloads.Latest(token);
                if (release == null) { Apply(defaults); Message("Aún no hay una versión pública en GitHub."); return; }
                var exe = release.Asset(Distribution.Launcher);
                Version available = release.NumericVersion();
                if (exe != null && available == null) throw new InvalidDataException("La release necesita una etiqueta como v0.1.1.");
                if (exe != null && available > Install.Current)
                {
                    Message("Descargando la versión " + available.ToString(3) + "…");
                    temp = install.Stage();
                    string path = Path.Combine(temp, Distribution.Launcher);
                    await downloads.GetAsset(release, exe, path, Distribution.LauncherLimit, Percent, token);
                    Message("Verificando la actualización…");
                    await Task.Run(() => Install.VerifyBuild(path, available), token);
                    token.ThrowIfCancellationRequested();
                    string next = install.Commit(path, available);
                    using (Install.Start(next, "--actualizado")) { }
                    restart = true;
                    View.Close();
                    return;
                }
                var config = release.Asset("servidor.json");
                if (config != null)
                {
                    temp = install.Stage();
                    string path = Path.Combine(temp, "servidor.json");
                    await downloads.GetAsset(release, config, path, 65536, null, token);
                    Apply(Settings.Parse(File.ReadAllText(path)));
                }
                else Apply(defaults);
                if (exe == null)
                    Message("La publicación de GitHub aún no incluye un launcher.");
                else
                    Message("Tu launcher está actualizado.");
            }
            catch (OperationCanceledException) { Message("La consulta tardó demasiado. Podés volver a intentarlo."); }
            catch (Exception ex) { Message("Actualización pendiente. " + Friendly(ex)); }
            finally { if (temp != null) Install.Discard(temp); if (!closed && !restart) SetBusy(false); }
        }

        async Task InstallClient()
        {
            if (busy || !settings.HasClient) return;
            installing = true;
            SetBusy(true);
            try
            {
                Message("Descargando el cliente multijugador…");
                Message(await game.Setup(settings, Percent, Message, closing.Token));
            }
            catch (Exception ex) { Message("Instalación pendiente. " + Friendly(ex)); }
            finally { installing = false; SetBusy(false); }
        }

        static string Friendly(Exception ex)
        {
            if (ex is System.Net.Http.HttpRequestException || ex is WebException)
                return "Revisá tu conexión y volvé a intentarlo.";
            return ex.Message;
        }
    }
}
