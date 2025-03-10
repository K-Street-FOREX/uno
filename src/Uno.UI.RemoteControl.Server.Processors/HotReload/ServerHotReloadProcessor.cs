﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Uno.Disposables;
using Uno.Extensions;
using Uno.UI.RemoteControl.HotReload.Messages;

[assembly: Uno.UI.RemoteControl.Host.ServerProcessorAttribute(typeof(Uno.UI.RemoteControl.Host.HotReload.ServerHotReloadProcessor))]

namespace Uno.UI.RemoteControl.Host.HotReload
{
	partial class ServerHotReloadProcessor : IServerProcessor, IDisposable
	{
		private FileSystemWatcher[] _watchers;
		private CompositeDisposable _watcherEventsDisposable;
		private IRemoteControlServer _remoteControlServer;

		public ServerHotReloadProcessor(IRemoteControlServer remoteControlServer)
		{
			_remoteControlServer = remoteControlServer;
		}

		public string Scope => "hotreload";

		public Task ProcessFrame(Frame frame)
		{
			switch (frame.Name)
			{
				case ConfigureServer.Name:
					ProcessConfigureServer(JsonConvert.DeserializeObject<ConfigureServer>(frame.Content));
					break;
				case XamlLoadError.Name:
					ProcessXamlLoadError(JsonConvert.DeserializeObject<XamlLoadError>(frame.Content));
					break;
			}

			return Task.CompletedTask;
		}

		private void ProcessXamlLoadError(XamlLoadError xamlLoadError)
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().LogError(
					$"The XAML file failed to load [{xamlLoadError.FilePath}]\n" +
					$"{xamlLoadError.ExceptionType}: {xamlLoadError.Message}\n" +
					$"{xamlLoadError.StackTrace}");
			}
		}

		private void ProcessConfigureServer(ConfigureServer configureServer)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().LogDebug($"Base project path: {configureServer.ProjectPath}");
				this.Log().LogDebug($"Xaml Search Paths: {string.Join(", ", configureServer.XamlPaths)}");
			}

#if NET6_0_OR_GREATER
			InitializeMetadataUpdater(configureServer);
#endif

			_watchers = configureServer.XamlPaths
				.Select(p => new FileSystemWatcher
				{
					Path = p,
					Filter = "*.*",
					NotifyFilter = NotifyFilters.LastWrite |
						NotifyFilters.Attributes |
						NotifyFilters.Size |
						NotifyFilters.CreationTime |
						NotifyFilters.FileName,
					EnableRaisingEvents = true,
					IncludeSubdirectories = false
				})
				.ToArray();

			_watcherEventsDisposable = new CompositeDisposable();

			foreach (var watcher in _watchers)
			{
				// Create an observable instead of using the FromEventPattern which
				// does not register to events properly.
				// Renames are required for the WriteTemporary->DeleteOriginal->RenameToOriginal that
				// Visual Studio uses to save files.

				var changes = Observable.Create<string>(o => {

					void changed(object s, FileSystemEventArgs args) => o.OnNext(args.FullPath);
					void renamed(object s, RenamedEventArgs args) => o.OnNext(args.FullPath);

					watcher.Changed += changed;
					watcher.Created += changed;
					watcher.Renamed += renamed;

					return Disposable.Create(() => {
						watcher.Changed -= changed;
						watcher.Created -= changed;
						watcher.Renamed -= renamed;
					});
				});

				var disposable = changes
					.Buffer(TimeSpan.FromMilliseconds(250))
					.Subscribe(filePaths =>
					{
						foreach (var file in filePaths.Distinct().Where(f => Path.GetExtension(f).Equals(".xaml", StringComparison.OrdinalIgnoreCase)))
						{
							OnXamlFileChanged(file);
						}
					}, e => Console.WriteLine($"Error {e}"));

				_watcherEventsDisposable.Add(disposable);
			}
		}

		private void OnXamlFileChanged(string fullPath)
			=> Task.Run(async () =>
			{
				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().LogDebug($"File {fullPath} changed");
				}

				await _remoteControlServer.SendFrame(
					new FileReload()
					{
						Content = File.ReadAllText(fullPath),
						FilePath = fullPath
					});
			});

		public void Dispose()
		{
			_watcherEventsDisposable?.Dispose();

			if (_watchers != null)
			{
				foreach (var watcher in _watchers)
				{
					watcher.Dispose();
				}
			}

#if NET6_0_OR_GREATER
			_solutionWatcherEventsDisposable?.Dispose();
			if (_solutionWatchers != null)
			{
				foreach (var watcher in _solutionWatchers)
				{
					watcher.Dispose();
				}
			}
#endif
		}
	}
}
