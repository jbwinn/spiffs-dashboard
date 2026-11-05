using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;

namespace SalesLedger.Core.Services
{
    /// <summary>Things the app asks Windows to do with a file. Behind an interface so tests never launch real programs.</summary>
    public interface IShellActions
    {
        /// <summary>Opens the file in its default app (the PDF viewer).</summary>
        void Open(string path);

        /// <summary>Sends the file to the default app's print command. False if that app doesn't support printing this way.</summary>
        bool Print(string path);

        /// <summary>Opens File Explorer with the file selected.</summary>
        void ShowInFolder(string path);

        /// <summary>Opens File Explorer at a folder.</summary>
        void OpenFolder(string folder);

        /// <summary>Puts the file on the clipboard, ready to paste into an email or chat. False if that failed.</summary>
        Task<bool> CopyFileToClipboard(string path);
    }

    public sealed class WindowsShellActions : IShellActions
    {
        public void Open(string path) =>
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

        public bool Print(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "print" });
                return true;
            }
            catch (Win32Exception)
            {
                // The default PDF app has no "print" command registered (common with browsers as PDF viewers)
                return false;
            }
        }

        public void ShowInFolder(string path) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

        public void OpenFolder(string folder) =>
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });

        public async Task<bool> CopyFileToClipboard(string path)
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
                || window.Clipboard is not { } clipboard)
            {
                return false;
            }

            var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            if (file == null)
            {
                return false;
            }

            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(file));
            await clipboard.SetDataAsync(data);
            return true;
        }
    }
}
