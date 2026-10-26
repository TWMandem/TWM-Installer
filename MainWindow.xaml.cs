
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TWM.Installer;

public partial class MainWindow : Window
{
    // Публичный репозиторий только для готовых релизов.
    // API GitHub не используется, поэтому установщик не зависит от API rate limit.
    private const string LatestManifestUrl =
        "https://github.com/TWMandem/TWM-Launcher-Releases/releases/latest/download/latest.json";

    private const string PackageDownloadBaseUrl =
        "https://github.com/TWMandem/TWM-Launcher-Releases/releases/latest/download/";

    private const string ReleaseManifestAssetName = "latest.json";
    private const string DefaultPackageAssetName = "TWM.Launcher.Package.zip";

    private static readonly HttpClient GitHubHttpClient = CreateGitHubHttpClient();

    private const string EmbeddedRuntimeResourceName =
        "TWM.Installer.Runtime.MicrosoftEdgeWebView2Setup.exe";

    private const string InstallerRegistryPath =
    @"Software\TWM\TWM Installer";

    private bool _isInstalling;

    private static readonly string[] WebView2RegistryPaths =
    {
        @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F1E...}",
        @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F1E...}"
    };


    private void RefreshInstallationState()
    {
        string? installPath = FindExistingInstallation();

        bool isInstalled = installPath != null;

        InstallButton.Visibility = isInstalled
            ? Visibility.Collapsed
            : Visibility.Visible;

        InstalledPanel.Visibility = isInstalled
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (isInstalled)
        {
            InstallationPathText.Text = installPath;
        }
    }

    private void OpenLauncher_Click(object sender, RoutedEventArgs e)
    {
        string? installPath = FindExistingInstallation();

        if (installPath == null)
        {
            RefreshInstallationState();

            ShowNotice(
                "Клиент не найден",
                "Не удалось обнаружить установленный TWM Launcher.",
                NoticeType.Warning);

            return;
        }

        string[] executableNames =
        {
        "TWM Launcher.exe",
        "AMM.App.exe"
    };

        string? executablePath = executableNames
            .Select(name => Path.Combine(installPath, name))
            .FirstOrDefault(File.Exists);

        if (executablePath == null)
        {
            ShowNotice(
                "Файл клиента не найден",
                "В папке установки отсутствует исполняемый файл лаунчера.",
                NoticeType.Error);

            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = installPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowNotice(
                "Не удалось открыть лаунчер",
                ex.Message,
                NoticeType.Error);
        }
    }


    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        string? installPath = FindExistingInstallation();

        if (installPath == null)
        {
            RefreshInstallationState();

            ShowNotice(
                "Клиент не найден",
                "Не удалось обнаружить установленный TWM Launcher. " +
                "Возможно, папка клиента уже удалена.",
                NoticeType.Warning);

            return;
        }

        bool confirmed = ShowConfirmation(
            "Удаление TWM Launcher",
            "Будет удалена папка установленного клиента:\n\n" +
            installPath +
            "\n\nЯрлык TWM Launcher на рабочем столе также будет удалён, " +
            "если он указывает на этот клиент.\n\n" +
            "Пользовательские данные пока не затрагиваются.\n\n" +
            "Продолжить?");

        if (!confirmed)
            return;

        // Отдельное согласие на удаление пользовательских данных.
        bool deleteUserData = ShowConfirmation(
            "Удалить пользовательские данные?",
            "Выбери «Продолжить», только если действительно хочешь удалить:\n\n" +
            "• настройки и данные в %LOCALAPPDATA%\\TWM Launcher;\n" +
            "• локальные сборки в C:\\TWM\\Builds\\Local;\n" +
            "• резервные копии в C:\\TWM\\Builds\\Backups;\n" +
            "• базу C:\\TWM\\Builds\\local-builds.db.\n\n" +
            "Если нажать «Отмена», перечисленные данные останутся на месте. " +
            "Папка C:\\TWM\\Builds целиком удаляться не будет.");

        try
        {
            if (IsLauncherRunningFromDirectory(installPath))
            {
                ShowNotice(
                    "Лаунчер запущен",
                    "Перед удалением полностью закрой TWM Launcher " +
                    "и повтори операцию.\n\n" +
                    "Файлы клиента не удалены.",
                    NoticeType.Warning);

                return;
            }

            // Удаляем только папку установки клиента.
            Directory.Delete(installPath, recursive: true);

            // Удаляем ярлык только в том случае, если он ведёт
            // к удалённой установке.
            DeleteDesktopShortcutForInstallation(installPath);

            // Удаляем сохранённый путь, чтобы установщик больше
            // не определял удалённый клиент как установленный.
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    InstallerRegistryPath,
                    writable: true);

                key?.DeleteValue("InstallPath", throwOnMissingValue: false);
            }
            catch
            {
                // Ошибка реестра не отменяет удаление клиента.
            }

            var dataErrors = new List<string>();

            if (deleteUserData)
            {
                dataErrors = DeleteUserData();
            }

            RefreshInstallationState();

            if (dataErrors.Count > 0)
            {
                ShowNotice(
                    "Клиент удалён не полностью",
                    "Файлы установленного клиента удалены, но некоторые " +
                    "пользовательские данные удалить не удалось.\n\n" +
                    string.Join("\n\n", dataErrors) +
                    "\n\nПроверь оставшиеся файлы вручную.",
                    NoticeType.Warning);

                return;
            }

            ShowNotice(
                "Удаление завершено",
                deleteUserData
                    ? "TWM Launcher и выбранные пользовательские данные удалены."
                    : "TWM Launcher удалён.\n\n" +
                      "Пользовательские данные, настройки, локальные сборки " +
                      "и резервные копии сохранены.",
                NoticeType.Success);
        }
        catch (Exception ex)
        {
            RefreshInstallationState();

            ShowNotice(
                "Ошибка удаления",
                "Не удалось полностью удалить TWM Launcher.\n\n" +
                ex.Message +
                "\n\nПроверь, не используются ли файлы клиента другими программами.",
                NoticeType.Error);
        }
    }


    private static bool IsLauncherRunningFromDirectory(string installPath)
    {
        string fullInstallPath = Path.GetFullPath(installPath)
            .TrimEnd(Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar);

        string[] executableNames =
        {
        "TWM Launcher.exe",
        "AMM.App.exe"
    };

        foreach (string executableName in executableNames)
        {
            foreach (Process process in Process.GetProcessesByName(
                         Path.GetFileNameWithoutExtension(executableName)))
            {
                using (process)
                {
                    try
                    {
                        string? processPath = process.MainModule?.FileName;

                        if (string.IsNullOrWhiteSpace(processPath))
                            continue;

                        string? processDirectory =
                            Path.GetDirectoryName(Path.GetFullPath(processPath));

                        if (string.Equals(
                                processDirectory?.TrimEnd(
                                    Path.DirectorySeparatorChar,
                                    Path.AltDirectorySeparatorChar),
                                fullInstallPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Процесс может завершиться или не разрешить
                        // чтение своего пути. Продолжаем проверку.
                    }
                }
            }
        }

        return false;
    }


    private static void DeleteDesktopShortcutForInstallation(
        string installPath)
    {
        string desktopPath = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);

        string shortcutPath = Path.Combine(
            desktopPath,
            "TWM Launcher.lnk");

        if (!File.Exists(shortcutPath))
            return;

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");

        if (shellType == null)
            return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);

        string? targetPath = shortcut.TargetPath as string;

        if (string.IsNullOrWhiteSpace(targetPath))
            return;

        string? targetDirectory = Path.GetDirectoryName(
            Path.GetFullPath(targetPath));

        string fullInstallPath = Path.GetFullPath(installPath)
            .TrimEnd(Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar);

        string? normalizedTargetDirectory = targetDirectory?
            .TrimEnd(Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar);

        if (string.Equals(
                normalizedTargetDirectory,
                fullInstallPath,
                StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(shortcutPath);
        }
    }


    private static List<string> DeleteUserData()
    {
        var errors = new List<string>();

        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        string[] directories =
        {
        Path.Combine(localAppData, "TWM Launcher"),
        @"C:\TWM\Builds\Local",
        @"C:\TWM\Builds\Backups"
    };

        foreach (string directory in directories)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"Не удалось удалить папку:\n{directory}\n{ex.Message}");
            }
        }

        string databasePath = @"C:\TWM\Builds\local-builds.db";

        try
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Не удалось удалить базу данных:\n{databasePath}\n{ex.Message}");
        }

        return errors;
    }


    public MainWindow()
    {
        InitializeComponent();

        RefreshInstallationState();


        Loaded += (_, _) =>
        {
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(450)
            };

            BeginAnimation(OpacityProperty, animation);
        };

    }

    private void TitleBar_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling)
        {
            ShowNotice(
                "Установка выполняется",
                "Дождись завершения установки перед закрытием программы.",
                NoticeType.Warning);

            return;
        }

        Close();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling)
            return;

        string? existingInstallPath = FindExistingInstallation();

        if (existingInstallPath != null)
        {
            ShowNotice(
                "TWM Launcher уже установлен",
                "Установщик обнаружил установленный клиент.\n\n" +
                "Расположение:\n" +
                existingInstallPath +
                "\n\nПовторная установка не запущена. " +
                "Сначала можно открыть установленный лаунчер " +
                "или перейти к его удалению.",
                NoticeType.Information);

            return;
        }

        string? temporaryDirectory = null;
        string? stagingDirectory = null;
        string? backupDirectory = null;
        string? installPath = null;

        bool oldInstallationMoved = false;
        bool newInstallationMoved = false;
        bool installationCommitted = false;

        InstallProgressWindow? progressWindow = null;

        try
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = "Выбери папку для установки TWM Launcher",
                Multiselect = false
            };

            if (folderDialog.ShowDialog(this) != true)
                return;

            installPath = Path.Combine(
                folderDialog.FolderName,
                "TWM Launcher");

            string transactionId = Guid.NewGuid().ToString("N");

            stagingDirectory = Path.Combine(
                folderDialog.FolderName,
                ".TWM Launcher.installing-" + transactionId);

            backupDirectory = Path.Combine(
                folderDialog.FolderName,
                ".TWM Launcher.backup-" + transactionId);

            if (Directory.Exists(installPath) &&
                Directory.EnumerateFileSystemEntries(installPath).Any())
            {
                bool overwrite = ShowConfirmation(
                    "Папка уже существует",
                    "В выбранной папке уже есть файлы.\n\n" +
                    "Установка может заменить файлы с совпадающими именами. " +
                    "Продолжить?");

                if (!overwrite)
                    return;
            }
            else
            {
                bool confirmed = ShowConfirmation(
                    "Установка TWM Launcher",
                    "Установщик загрузит последнюю стабильную версию из " +
                    "официального GitHub Releases, проверит контрольные суммы " +
                    "и установит TWM Launcher в папку:\n\n" +
                    installPath +
                    "\n\nПродолжить?");

                if (!confirmed)
                    return;
            }

            _isInstalling = true;
            IsEnabled = false;

            progressWindow = new InstallProgressWindow();
            progressWindow.Owner = this;
            progressWindow.Show();

            IProgress<InstallProgress> progress = new Progress<InstallProgress>(
                p => progressWindow.UpdateProgress(p));

            temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "TWM-Installer-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(temporaryDirectory);

            progress.Report(new InstallProgress(
                "Поиск последней стабильной версии на GitHub...",
                2));

            string packageZipPath = await DownloadLatestPackageAsync(
                temporaryDirectory,
                progress);

            progress.Report(new InstallProgress(
                "Проверка загруженного пакета...",
                20));

            string packageFilesDirectory = Path.Combine(
                temporaryDirectory,
                "Files");

            int fileCount = await Task.Run(() =>
                ExtractAndVerifyPackage(
                    packageZipPath,
                    packageFilesDirectory,
                    progress));

            progress.Report(new InstallProgress(
                "Проверка пакета завершена. Подготовка установки...",
                20));

            await Task.Run(() =>
            {
                Directory.CreateDirectory(stagingDirectory!);

                CopyDirectory(
                    packageFilesDirectory,
                    stagingDirectory!,
                    fileCount,
                    progress);
            });

            progress.Report(new InstallProgress(
                "Проверка WebView2...",
                92));

            await EnsureWebView2RuntimeAsync(progress);

            // Сохраняем существующую установку.
            if (Directory.Exists(installPath))
            {
                Directory.Move(installPath, backupDirectory!);
                oldInstallationMoved = true;
            }

            // Перемещаем подготовленную версию на место установки.
            Directory.Move(stagingDirectory!, installPath);
            newInstallationMoved = true;

            progress.Report(new InstallProgress(
                "Проверка установленных файлов...",
                94));

            await Task.Run(() =>
                VerifyDirectoryMatchesPackage(
                    packageFilesDirectory,
                    installPath));

            progress.Report(new InstallProgress(
                "Файлы успешно проверены. Создание ярлыка...",
                97));

            CreateDesktopShortcut(installPath);
            CreateStartMenuShortcut(installPath);

            // Основные операции завершены.
            installationCommitted = true;

            // Сохраняем путь для последующего обнаружения клиента.
            SaveInstallationPath(installPath);

            // Сразу обновляем интерфейс после установки.
            RefreshInstallationState();

            // Удаляем резервную копию только после успешной установки.
            // Если удалить её не удалось, оставляем её для ручного удаления.
            if (oldInstallationMoved && Directory.Exists(backupDirectory))
            {
                try
                {
                    Directory.Delete(backupDirectory, recursive: true);
                    oldInstallationMoved = false;
                }
                catch
                {
                    // Не отменяем успешную установку из-за очистки резервной копии.
                }
            }

            progress.Report(new InstallProgress(
                "Установка завершена!",
                100));

            await Task.Delay(400);

            progressWindow.Close();
            progressWindow = null;

            ShowNotice(
                "Установка завершена",
                "TWM Launcher успешно установлен.\n\n" +
                "Ярлык создан на рабочем столе.",
                NoticeType.Success);
        }
        catch (Exception ex)
        {
            progressWindow?.Close();
            progressWindow = null;

            string rollbackMessage = "";


            if (!installationCommitted)
            {
                var rollbackErrors = new List<string>();

                // Шаг 1. Независимо пытаемся удалить неполную новую версию.
                if (newInstallationMoved &&
                    !string.IsNullOrWhiteSpace(installPath) &&
                    Directory.Exists(installPath))
                {
                    try
                    {
                        Directory.Delete(installPath, recursive: true);
                        newInstallationMoved = false;
                    }
                    catch (Exception rollbackEx)
                    {
                        rollbackErrors.Add(
                            "Не удалось удалить неполную новую версию: " +
                            rollbackEx.Message);
                    }
                }

                // Шаг 2. Отдельно пытаемся восстановить старую версию.
                if (oldInstallationMoved &&
                    !string.IsNullOrWhiteSpace(installPath) &&
                    !string.IsNullOrWhiteSpace(backupDirectory) &&
                    Directory.Exists(backupDirectory))
                {
                    try
                    {
                        if (Directory.Exists(installPath))
                        {
                            throw new IOException(
                                "Каталог установки всё ещё существует. " +
                                "Старая версия сохранена в резервной копии.");
                        }

                        Directory.Move(backupDirectory, installPath);
                        oldInstallationMoved = false;
                    }
                    catch (Exception rollbackEx)
                    {
                        rollbackErrors.Add(
                            "Не удалось восстановить предыдущую версию: " +
                            rollbackEx.Message);
                    }
                }

                // Шаг 3. Не удаляем резервную копию, если восстановление не удалось.
                if (oldInstallationMoved &&
                    !string.IsNullOrWhiteSpace(backupDirectory) &&
                    Directory.Exists(backupDirectory))
                {
                    rollbackErrors.Add(
                        "Резервная копия старой версии сохранена:\n" +
                        backupDirectory);
                }

                if (rollbackErrors.Count > 0)
                {
                    rollbackMessage =
                        "\n\nРезультат автоматического отката:\n" +
                        string.Join("\n\n", rollbackErrors);
                }
            }


            ShowNotice(
                "Ошибка установки",
                "Установить TWM Launcher не удалось.\n\n" +
                ex.Message +
                rollbackMessage,
                NoticeType.Error);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(stagingDirectory))
            {
                try
                {
                    if (Directory.Exists(stagingDirectory))
                    {
                        Directory.Delete(stagingDirectory, recursive: true);
                    }
                }
                catch
                {
                    // Если папка занята, её можно удалить вручную.
                }
            }

            if (!string.IsNullOrWhiteSpace(temporaryDirectory))
            {
                try
                {
                    if (Directory.Exists(temporaryDirectory))
                        Directory.Delete(temporaryDirectory, true);
                }
                catch
                {
                    // Временные файлы можно удалить вручную,
                    // если их использует другая программа.
                }
            }

            _isInstalling = false;
            IsEnabled = true;
        }
    }

    private static HttpClient CreateGitHubHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TWM-Installer/1.0");
        return client;
    }

    private static async Task<string> DownloadLatestPackageAsync(
        string temporaryDirectory,
        IProgress<InstallProgress> progress)
    {
        // latest.json — статический файл в публичном репозитории релизов.
        // Не используем api.github.com: анонимные API-запросы имеют rate limit.
        progress.Report(new InstallProgress(
            "Получение информации о последней версии...",
            3));

        using var manifestResponse = await GitHubHttpClient.GetAsync(
            LatestManifestUrl,
            HttpCompletionOption.ResponseHeadersRead);
        manifestResponse.EnsureSuccessStatusCode();

        await using var manifestStream = await manifestResponse.Content.ReadAsStreamAsync();
        var releaseManifest = await JsonSerializer.DeserializeAsync<ReleaseManifest>(
            manifestStream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (releaseManifest == null ||
            string.IsNullOrWhiteSpace(releaseManifest.PackageVersion) ||
            string.IsNullOrWhiteSpace(releaseManifest.PackageFile) ||
            string.IsNullOrWhiteSpace(releaseManifest.Sha256) ||
            !Regex.IsMatch(releaseManifest.Sha256, @"\A[0-9a-fA-F]{64}\z"))
        {
            throw new InvalidDataException(
                $"Файл {ReleaseManifestAssetName} имеет неверный формат. " +
                "Ожидаются packageVersion, packageFile и sha256.");
        }

        // Запрещаем пути и произвольные имена из манифеста.
        // Установщик должен загружать только ожидаемый ZIP-пакет.
        if (!string.Equals(
                releaseManifest.PackageFile,
                DefaultPackageAssetName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Недопустимое имя пакета в манифесте: {releaseManifest.PackageFile}");
        }

        string packageUrl = PackageDownloadBaseUrl + DefaultPackageAssetName;
        string packagePath = Path.Combine(temporaryDirectory, DefaultPackageAssetName);

        progress.Report(new InstallProgress(
            $"Загрузка TWM Launcher {releaseManifest.PackageVersion}...",
            4));

        using var packageResponse = await GitHubHttpClient.GetAsync(
            packageUrl,
            HttpCompletionOption.ResponseHeadersRead);
        packageResponse.EnsureSuccessStatusCode();

        long? contentLength = packageResponse.Content.Headers.ContentLength;
        await using var networkStream = await packageResponse.Content.ReadAsStreamAsync();
        await using (var output = new FileStream(
            packagePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            useAsync: true))
        {
            var buffer = new byte[1024 * 128];
            long totalRead = 0;
            int read;

            while ((read = await networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;

                int percent = contentLength is > 0
                    ? 4 + (int)Math.Min(14, totalRead * 14d / contentLength.Value)
                    : 4;
                string sizeText = contentLength is > 0
                    ? $"{FormatBytes(totalRead)} из {FormatBytes(contentLength.Value)}"
                    : FormatBytes(totalRead);
                progress.Report(new InstallProgress(
                    $"Загрузка пакета TWM Launcher: {sizeText}",
                    percent));
            }
        }

        // Если сервер передал размер, убеждаемся, что загрузка полная.
        if (contentLength is > 0 && new FileInfo(packagePath).Length != contentLength.Value)
        {
            try { File.Delete(packagePath); } catch { }
            throw new InvalidDataException(
                "Размер загруженного пакета не совпадает с Content-Length. " +
                "Проверь подключение к интернету и повтори попытку.");
        }

        string actualPackageHash;
        await using (var packageFileStream = File.OpenRead(packagePath))
        {
            actualPackageHash = Convert.ToHexString(
                await SHA256.HashDataAsync(packageFileStream));
        }

        if (!actualPackageHash.Equals(
                releaseManifest.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(packagePath); } catch { }
            throw new InvalidDataException(
                "SHA-256 загруженного пакета не совпадает с latest.json. " +
                "Файл может быть повреждён; установка отменена.");
        }

        progress.Report(new InstallProgress(
            $"Пакет {releaseManifest.PackageVersion} загружен и проверен.",
            19));

        return packagePath;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.0} {units[unit]}";
    }

    private static int ExtractAndVerifyPackage(
        string packageZipPath,
        string packageFilesDirectory,
        IProgress<InstallProgress> progress)
    {
        using var packageStream = new FileStream(
            packageZipPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        using var archive = new ZipArchive(
            packageStream,
            ZipArchiveMode.Read,
            leaveOpen: false);

        var entries = new Dictionary<string, ZipArchiveEntry>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            string normalizedName = entry.FullName.Replace('\\', '/');

            if (normalizedName.StartsWith("/", StringComparison.Ordinal) ||
                normalizedName.Contains(':'))
            {
                throw new InvalidDataException(
                    $"Недопустимый путь в ZIP: {entry.FullName}");
            }

            if (!entries.TryAdd(normalizedName, entry))
            {
                throw new InvalidDataException(
                    $"В ZIP обнаружен повторяющийся путь: {normalizedName}");
            }
        }

        if (!entries.TryGetValue("manifest.json", out var manifestEntry))
        {
            throw new InvalidDataException(
                "В ZIP отсутствует manifest.json.");
        }

        ManifestRoot? manifest;

        using (var manifestStream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<ManifestRoot>(
                manifestStream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }

        if (manifest?.Files == null || manifest.Files.Count == 0)
        {
            throw new InvalidDataException(
                "manifest.json не содержит списка файлов.");
        }

        var manifestPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                throw new InvalidDataException(
                    "В манифесте найден файл без пути.");
            }

            string relativePath = file.Path.Replace('\\', '/');

            if (Path.IsPathRooted(relativePath) ||
                relativePath.StartsWith("/", StringComparison.Ordinal) ||
                relativePath.Contains(':') ||
                relativePath.Split('/').Any(
                    part => part == ".." || part == "." || part.Length == 0))
            {
                throw new InvalidDataException(
                    $"Недопустимый путь в манифесте: {file.Path}");
            }

            if (!manifestPaths.Add(relativePath))
            {
                throw new InvalidDataException(
                    $"Повторяющийся путь в манифесте: {relativePath}");
            }

            if (file.Size < 0)
            {
                throw new InvalidDataException(
                    $"Некорректный размер файла: {relativePath}");
            }

            if (string.IsNullOrWhiteSpace(file.Sha256) ||
                !Regex.IsMatch(file.Sha256, @"\A[0-9a-fA-F]{64}\z"))
            {
                throw new InvalidDataException(
                    $"Некорректный SHA-256 в манифесте: {relativePath}");
            }
        }

        if (entries.Count != manifest.Files.Count + 1)
        {
            throw new InvalidDataException(
                "Количество записей в ZIP не совпадает с манифестом.");
        }

        string fullRoot = Path.GetFullPath(packageFilesDirectory);
        Directory.CreateDirectory(fullRoot);

        string rootPrefix = fullRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        int totalFiles = manifest.Files.Count;
        int processedFiles = 0;

        foreach (var file in manifest.Files)
        {
            string relativePath = file.Path.Replace('\\', '/');
            string zipPath = "Files/" + relativePath;

            if (!entries.TryGetValue(zipPath, out var entry))
            {
                throw new InvalidDataException(
                    $"Файл отсутствует в ZIP: {relativePath}");
            }

            string destination = Path.GetFullPath(
                Path.Combine(
                    fullRoot,
                    relativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));

            if (!destination.StartsWith(
                    rootPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Путь файла выходит за пределы каталога пакета: {relativePath}");
            }

            string? destinationDirectory =
                Path.GetDirectoryName(destination);

            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                throw new InvalidDataException(
                    $"Не удалось определить папку файла: {relativePath}");
            }

            Directory.CreateDirectory(destinationDirectory);

            using (var input = entry.Open())
            using (var output = new FileStream(
                       destination,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                input.CopyTo(output);
            }

            var fileInfo = new FileInfo(destination);

            if (fileInfo.Length != file.Size)
            {
                throw new InvalidDataException(
                    $"Размер файла не совпадает с манифестом: {relativePath}");
            }

            string actualHash;

            using (var fileStream = File.OpenRead(destination))
            {
                actualHash = Convert.ToHexString(
                    SHA256.HashData(fileStream));
            }

            if (!actualHash.Equals(
                    file.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SHA-256 не совпадает: {relativePath}");
            }

            processedFiles++;

            int percent = 3 + (int)(
                processedFiles / (double)totalFiles * 15);

            progress.Report(new InstallProgress(
                $"Проверка файлов: {processedFiles} из {totalFiles}",
                percent));
        }

        return totalFiles;
    }

    private static void VerifyDirectoryMatchesPackage(
    string expectedDirectory,
    string actualDirectory)
    {
        if (!Directory.Exists(expectedDirectory))
        {
            throw new DirectoryNotFoundException(
                "Не найден каталог проверенного пакета.");
        }

        if (!Directory.Exists(actualDirectory))
        {
            throw new DirectoryNotFoundException(
                "Не найден каталог установленного TWM Launcher.");
        }

        var expectedFiles = Directory
            .GetFiles(expectedDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(expectedDirectory, path),
                path => path,
                StringComparer.OrdinalIgnoreCase);

        var actualFiles = Directory
            .GetFiles(actualDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(actualDirectory, path),
                path => path,
                StringComparer.OrdinalIgnoreCase);

        if (expectedFiles.Count != actualFiles.Count)
        {
            throw new InvalidDataException(
                "Количество установленных файлов не совпадает " +
                "с проверенным пакетом.");
        }

        foreach (var expected in expectedFiles)
        {
            if (!actualFiles.TryGetValue(
                    expected.Key,
                    out string? actualPath))
            {
                throw new InvalidDataException(
                    $"После установки отсутствует файл: {expected.Key}");
            }

            var expectedInfo = new FileInfo(expected.Value);
            var actualInfo = new FileInfo(actualPath);

            if (expectedInfo.Length != actualInfo.Length)
            {
                throw new InvalidDataException(
                    $"Размер установленного файла не совпадает: " +
                    expected.Key);
            }

            string expectedHash;
            using (var stream = File.OpenRead(expected.Value))
            {
                expectedHash = Convert.ToHexString(
                    SHA256.HashData(stream));
            }

            string actualHash;
            using (var stream = File.OpenRead(actualPath))
            {
                actualHash = Convert.ToHexString(
                    SHA256.HashData(stream));
            }

            if (!expectedHash.Equals(
                    actualHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Контрольная сумма установленного файла не совпадает: " +
                    expected.Key);
            }
        }
    }

    private static void CopyDirectory(
        string source,
        string destination,
        int totalFiles,
        IProgress<InstallProgress> progress)
    {
        Directory.CreateDirectory(destination);

        var files = Directory.GetFiles(
            source,
            "*",
            SearchOption.AllDirectories);

        int copiedFiles = 0;

        foreach (string sourceFile in files)
        {
            string relativePath = Path.GetRelativePath(
                source,
                sourceFile);

            string destinationFile = Path.Combine(
                destination,
                relativePath);

            string? destinationDirectory =
                Path.GetDirectoryName(destinationFile);

            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourceFile, destinationFile, overwrite: true);

            copiedFiles++;

            int percent = 20 + (int)(
                copiedFiles / (double)Math.Max(totalFiles, 1) * 70);

            progress.Report(new InstallProgress(
                $"Копирование файлов: {copiedFiles} из {totalFiles}",
                Math.Min(percent, 90)));
        }
    }

    private static async Task EnsureWebView2RuntimeAsync(
        IProgress<InstallProgress> progress)
    {
        if (IsWebView2RuntimeInstalled())
            return;

        progress.Report(new InstallProgress(
            "Подготовка установки Microsoft Edge WebView2...",
            93));

        string runtimePath = Path.Combine(
            Path.GetTempPath(),
            "TWM-WebView2-" + Guid.NewGuid().ToString("N") + ".exe");

        try
        {
            using var resource =
                typeof(MainWindow).Assembly.GetManifestResourceStream(
                    EmbeddedRuntimeResourceName)
                ?? throw new InvalidOperationException(
                    "В установщик не встроен MicrosoftEdgeWebView2Setup.exe.");

            using (var output = new FileStream(
                       runtimePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                await resource.CopyToAsync(output);
            }

            progress.Report(new InstallProgress(
                "Установка Microsoft Edge WebView2 Runtime...",
                95));

            var startInfo = new ProcessStartInfo
            {
                FileName = runtimePath,
                Arguments = "/silent /install",
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Не удалось запустить установщик WebView2.");

            await process.WaitForExitAsync();

            if (process.ExitCode != 0 && process.ExitCode != 3010)
            {
                throw new InvalidOperationException(
                    $"Установка WebView2 завершилась с кодом {process.ExitCode}.");
            }

            if (!IsWebView2RuntimeInstalled())
            {
                throw new InvalidOperationException(
                    "Установщик WebView2 завершился, но Runtime не обнаружен.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(runtimePath))
                    File.Delete(runtimePath);
            }
            catch
            {
                // Временный установщик может быть занят системой.
            }
        }
    }


    private static bool IsWebView2RuntimeInstalled()
    {
        // 1. Проверяем стандартную папку WebView2 в профиле пользователя.
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        string runtimeDirectory = Path.Combine(
            localAppData,
            "Microsoft",
            "EdgeWebView",
            "Application");

        if (HasWebView2Version(runtimeDirectory))
            return true;

        // 2. Проверяем реестр для текущего пользователя и компьютера.
        string[] registryPaths =
        {
        @"Software\Microsoft\EdgeUpdate\Clients\{F1E...}",
        @"Software\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F1E...}",
        @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        @"Software\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
    };

        RegistryKey[] registryRoots =
        {
        Registry.CurrentUser,
        Registry.LocalMachine
    };

        foreach (var root in registryRoots)
        {
            foreach (string registryPath in registryPaths)
            {
                try
                {
                    using var key = root.OpenSubKey(registryPath);

                    if (key?.GetValue("pv") is string version &&
                        Version.TryParse(version, out var parsedVersion) &&
                        parsedVersion > new Version(0, 0, 0, 0))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Продолжаем проверку остальных расположений.
                }
            }
        }

        // 3. Проверяем типичные каталоги установки Runtime.
        string programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);

        string programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);

        string[] possibleDirectories =
        {
        Path.Combine(programFiles, "Microsoft", "EdgeWebView", "Application"),
        Path.Combine(programFilesX86, "Microsoft", "EdgeWebView", "Application")
    };

        return possibleDirectories.Any(HasWebView2Version);
    }

    private static bool HasWebView2Version(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return false;

            foreach (string versionDirectory in Directory.GetDirectories(directory))
            {
                string loaderPath = Path.Combine(
                    versionDirectory,
                    "EBWebView",
                    "x64",
                    "EmbeddedBrowserWebView.dll");

                if (File.Exists(loaderPath))
                    return true;

                string[] architecturePaths =
                {
                Path.Combine(versionDirectory, "msedgewebview2.exe"),
                Path.Combine(versionDirectory, "EBWebView", "msedgewebview2.exe")
            };

                if (architecturePaths.Any(File.Exists))
                    return true;
            }
        }
        catch
        {
            // Папка может быть недоступна для чтения.
        }

        return false;
    }

    private static string? FindExistingInstallation()
    {
        // 1. Проверяем путь, сохранённый предыдущей установкой.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                InstallerRegistryPath);

            string? savedPath = key?.GetValue("InstallPath") as string;

            if (IsValidInstallationDirectory(savedPath))
                return Path.GetFullPath(savedPath!);
        }
        catch
        {
            // Если запись отсутствует или недоступна,
            // продолжаем поиск другими способами.
        }

        // 2. Проверяем ярлык на рабочем столе.
        try
        {
            string desktopPath = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);

            string shortcutPath = Path.Combine(
                desktopPath,
                "TWM Launcher.lnk");

            if (File.Exists(shortcutPath))
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");

                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);

                    string? targetPath = shortcut.TargetPath as string;

                    if (!string.IsNullOrWhiteSpace(targetPath) &&
                        File.Exists(targetPath))
                    {
                        string? directory =
                            Path.GetDirectoryName(targetPath);

                        if (IsValidInstallationDirectory(directory))
                            return Path.GetFullPath(directory!);
                    }
                }
            }
        }
        catch
        {
            // Ошибка чтения ярлыка не должна мешать запуску установщика.
        }

        return null;
    }

    private static bool IsValidInstallationDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        try
        {
            if (!Directory.Exists(directory))
                return false;

            string[] executableNames =
            {
            "TWM Launcher.exe",
            "AMM.App.exe"
        };

            return executableNames.Any(name =>
                File.Exists(Path.Combine(directory, name)));
        }
        catch
        {
            return false;
        }
    }

    private static void SaveInstallationPath(string installPath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                InstallerRegistryPath);

            key?.SetValue(
                "InstallPath",
                Path.GetFullPath(installPath),
                RegistryValueKind.String);
        }
        catch
        {
            // Если сохранить путь не удалось, обнаружение
            // всё ещё может сработать через ярлык на рабочем столе.
        }
    }


    private static void CreateDesktopShortcut(string installPath)
    {
        string desktopPath = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);

        string shortcutPath = Path.Combine(
            desktopPath,
            "TWM Launcher.lnk");

        string? launcherPath = Directory
            .GetFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
                Path.GetFileName(path).Equals(
                    "TWM Launcher.exe",
                    StringComparison.OrdinalIgnoreCase));

        launcherPath ??= Directory
            .GetFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
                Path.GetFileName(path).Equals(
                    "AMM.App.exe",
                    StringComparison.OrdinalIgnoreCase));

        if (launcherPath == null)
        {
            throw new FileNotFoundException(
                "В папке установки не найден исполняемый файл TWM Launcher.");
        }

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");

        if (shellType == null)
        {
            throw new InvalidOperationException(
                "Не удалось создать ярлык: WScript.Shell недоступен.");
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);

        shortcut.TargetPath = launcherPath;
        shortcut.WorkingDirectory = installPath;
        shortcut.Description = "TWM Launcher";
        shortcut.IconLocation = launcherPath + ",0";
        shortcut.Save();
    }

    private static void CreateStartMenuShortcut(string installPath)
    {
        string? launcherPath = new[] { "TWM Launcher.exe", "AMM.App.exe" }
            .Select(name => Path.Combine(installPath, name))
            .FirstOrDefault(File.Exists);

        if (launcherPath == null)
            throw new FileNotFoundException("В папке установки не найден исполняемый файл TWM Launcher.");

        // Сначала пробуем общий каталог меню «Пуск», как у обычной установки.
        string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        string userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string[] candidateDirectories = { commonPrograms, userPrograms };
        Exception? lastError = null;

        foreach (string programsDirectory in candidateDirectories.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(programsDirectory);
                string shortcutPath = Path.Combine(programsDirectory, "TWM Launcher.lnk");
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                    throw new InvalidOperationException("WScript.Shell недоступен для создания ярлыка.");

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = launcherPath;
                shortcut.WorkingDirectory = installPath;
                shortcut.Description = "TWM Launcher";
                shortcut.IconLocation = launcherPath + ",0";
                shortcut.Save();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new IOException("Не удалось создать ярлык TWM Launcher в меню «Пуск».", lastError);
    }

    private bool ShowConfirmation(string title, string message)
    {
        var dialog = new NoticeWindow(
            title,
            message,
            NoticeType.Confirmation)
        {
            Owner = this
        };

        return dialog.ShowDialog() == true;
    }

    private void ShowNotice(
        string title,
        string message,
        NoticeType type)
    {
        var dialog = new NoticeWindow(
            title,
            message,
            type)
        {
            Owner = this
        };

        dialog.ShowDialog();
    }

    private sealed class ReleaseManifest
    {
        public string? PackageVersion { get; set; }
        public string? PackageFile { get; set; }
        public string? Sha256 { get; set; }
    }

    private sealed class ManifestRoot
    {
        public string? PackageVersion { get; set; }

        public List<ManifestFile> Files { get; set; } = new();
    }

    private sealed class ManifestFile
    {
        public string Path { get; set; } = "";

        public long Size { get; set; }

        public string Sha256 { get; set; } = "";
    }
}

public sealed record InstallProgress(string Message, int Percent);

public enum NoticeType
{
    Information,
    Success,
    Warning,
    Error,
    Confirmation
}

public sealed class NoticeWindow : Window
{
    private static readonly Brush BackgroundBrush =
        new SolidColorBrush(Color.FromRgb(13, 15, 19));

    private static readonly Brush PanelBrush =
        new SolidColorBrush(Color.FromRgb(20, 23, 29));

    private static readonly Brush SilverBrush =
        new SolidColorBrush(Color.FromRgb(192, 197, 206));

    private static readonly Brush MutedBrush =
        new SolidColorBrush(Color.FromRgb(137, 144, 155));

    private static readonly Brush BorderBrushSilver =
        new SolidColorBrush(Color.FromRgb(55, 61, 72));

    public NoticeWindow(
        string title,
        string message,
        NoticeType type)
    {
        Title = title;
        Width = 470;
        SizeToContent = SizeToContent.Height;
        MinHeight = 225;
        MaxHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = BackgroundBrush;
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        ShowInTaskbar = false;

        var outerBorder = new Border
        {
            Background = BackgroundBrush,
            BorderBrush = BorderBrushSilver,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true
        };

        var root = new Grid();

        root.RowDefinitions.Add(
            new RowDefinition { Height = new GridLength(48) });

        root.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });

        // Собственная верхняя панель.
        var titleBar = new Border
        {
            Background = PanelBrush,
            BorderBrush = BorderBrushSilver,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 0, 8, 0)
        };

        var titleGrid = new Grid();

        titleGrid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        titleGrid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = title,
            Foreground = SilverBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        titleGrid.Children.Add(titleText);

        var closeButton = CreateButton("×", false);
        closeButton.Width = 38;
        closeButton.Height = 32;
        closeButton.FontSize = 21;
        closeButton.Click += (_, _) =>
        {
            if (type == NoticeType.Confirmation)
                DialogResult = false;
            else
                Close();
        };

        Grid.SetColumn(closeButton, 1);
        titleGrid.Children.Add(closeButton);

        titleBar.Child = titleGrid;

        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        };

        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        // Содержимое уведомления.
        var content = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 22)
        };

        string symbol = type switch
        {
            NoticeType.Success => "✓",
            NoticeType.Warning => "!",
            NoticeType.Error => "×",
            NoticeType.Confirmation => "?",
            _ => "i"
        };

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 15)
        };

        heading.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(31, 35, 43)),
            BorderBrush = BorderBrushSilver,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = symbol,
                Foreground = SilverBrush,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        });

        heading.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 0, 0),
            MaxWidth = 350
        });

        content.Children.Add(heading);

        content.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = MutedBrush,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Margin = new Thickness(0, 0, 0, 24)
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        if (type == NoticeType.Confirmation)
        {
            var cancelButton = CreateButton("Отмена", false);
            cancelButton.MinWidth = 100;
            cancelButton.Margin = new Thickness(0, 0, 10, 0);

            cancelButton.Click += (_, _) => DialogResult = false;

            buttons.Children.Add(cancelButton);
        }

        var acceptButton = CreateButton(
            type == NoticeType.Confirmation ? "Продолжить" : "Понятно",
            true);

        acceptButton.MinWidth = 115;
        acceptButton.IsDefault = true;

        acceptButton.Click += (_, _) =>
        {
            if (type == NoticeType.Confirmation)
                DialogResult = true;
            else
                Close();
        };

        buttons.Children.Add(acceptButton);
        content.Children.Add(buttons);

        Grid.SetRow(content, 1);
        root.Children.Add(content);

        outerBorder.Child = root;
        Content = outerBorder;
    }

    private static Button CreateButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 38,
            Padding = new Thickness(15, 7, 15, 7),
            Background = primary
                ? SilverBrush
                : PanelBrush,
            Foreground = primary
                ? new SolidColorBrush(Color.FromRgb(13, 15, 19))
                : SilverBrush,
            BorderBrush = BorderBrushSilver,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null
        };

        var template = new ControlTemplate(typeof(Button));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(
            Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(
            Border.BorderBrushProperty,
            new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(
            Border.BorderThicknessProperty,
            new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(
            Border.CornerRadiusProperty,
            new CornerRadius(5));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(
            HorizontalAlignmentProperty,
            HorizontalAlignment.Center);
        presenter.SetValue(
            VerticalAlignmentProperty,
            VerticalAlignment.Center);

        border.AppendChild(presenter);
        template.VisualTree = border;

        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };

        hover.Setters.Add(new Setter(
            Control.BackgroundProperty,
            primary
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(39, 44, 53))));

        template.Triggers.Add(hover);
        button.Template = template;

        return button;
    }
}

public sealed class InstallProgressWindow : Window
{
    private readonly TextBlock _messageText;
    private readonly TextBlock _percentText;
    private readonly ProgressBar _progressBar;

    public InstallProgressWindow()
    {
        Title = "Установка TWM Launcher";
        Width = 510;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromRgb(13, 15, 19));
        Foreground = new SolidColorBrush(Color.FromRgb(242, 243, 245));
        FontFamily = new FontFamily("Segoe UI");
        ShowInTaskbar = false;

        var silver = new SolidColorBrush(Color.FromRgb(192, 197, 206));
        var muted = new SolidColorBrush(Color.FromRgb(137, 144, 155));
        var panel = new SolidColorBrush(Color.FromRgb(20, 23, 29));
        var borderBrush = new SolidColorBrush(Color.FromRgb(55, 61, 72));

        var outer = new Border
        {
            Background = Background,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true
        };

        var root = new Grid();

        root.RowDefinitions.Add(
            new RowDefinition { Height = new GridLength(48) });

        root.RowDefinitions.Add(
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Кастомный заголовок.
        var titleBar = new Border
        {
            Background = panel,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 0, 0, 0),
            Child = new TextBlock
            {
                Text = "TWM  /  INSTALLATION",
                Foreground = silver,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        };

        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 18)
        };

        content.Children.Add(new TextBlock
        {
            Text = "Установка TWM Launcher",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8)
        });

        _messageText = new TextBlock
        {
            Text = "Подготовка...",
            FontSize = 12,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        };

        content.Children.Add(_messageText);

        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 8,
            Value = 0,
            Background = new SolidColorBrush(Color.FromRgb(35, 39, 47)),
            Foreground = silver,
            BorderThickness = new Thickness(0)
        };

        content.Children.Add(_progressBar);

        _percentText = new TextBlock
        {
            Text = "0%",
            FontSize = 11,
            Foreground = silver,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 9, 0, 0)
        };

        content.Children.Add(_percentText);

        Grid.SetRow(content, 1);
        root.Children.Add(content);

        outer.Child = root;
        Content = outer;
    }

    public void UpdateProgress(InstallProgress progress)
    {
        _messageText.Text = progress.Message;
        _progressBar.Value = Math.Clamp(progress.Percent, 0, 100);
        _percentText.Text = $"{_progressBar.Value:0}%";
    }
}

