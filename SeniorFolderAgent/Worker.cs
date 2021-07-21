using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SeniorFolderAgent
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;
        private readonly string printingFolders = "Folders";
        private readonly string formatType = "Format";
        private readonly string printerAgent = "Sumatra_Executable";

        private ConcurrentQueue<FileModel> FileQueue;
        private CancellationToken _cancellationToken;
        private List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private Dictionary<string, string> gatesPrinter = new Dictionary<string, string>();
        private string ToBeDeletedFolder = "";
        private string printerAgentExecutable = "";
        private int pdfCreationThreadWait = 0;

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            FileQueue = new ConcurrentQueue<FileModel>();
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _logger.LogDebug("Thread Id in StartAsync : {0}", Thread.CurrentThread.ManagedThreadId);
            //subscribe to event with an event handler to process the file

            // get all the folders to watch
            try
            {
                foreach (var item in _configuration.GetSection(printingFolders).GetChildren())
                {
                    var gateFolder = "";
                    var gatePrinter = "";
                    foreach (var val in item.GetChildren())
                    {
                        if (val.Key == "Folder")
                        {
                            gateFolder = val.Value;

                            Watcher(val.Value);
                            _logger.LogInformation("Added folder {0} to tracked folders", val.Value);
                        }
                        if (val.Key == "Printer")
                        {
                            gatePrinter = val.Value;
                        }
                    }

                    gatesPrinter.Add(gateFolder, gatePrinter);
                }

                ToBeDeletedFolder = _configuration.GetSection("ToBeDeleted").Value;
                printerAgentExecutable = _configuration.GetSection(printerAgent).Value;
                pdfCreationThreadWait = int.Parse(_configuration.GetSection("PDFWaitCreationTime").Value)*1000;
            }
            catch (Exception ex)
            {
                _logger.LogError("Service not initialized. {0}-{1}", ex.Message, ex.StackTrace);
                StopAsync(_cancellationToken);
            }
            return base.StartAsync(cancellationToken);
        }


        public override Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var watcher in watchers)
            {
                if (watcher != null)
                {
                    watcher.Error -= OnError;
                    watcher.Created -= OnFileCreated;
                    watcher.Dispose();
                }
            }

            _logger.LogInformation("Service stopped");
            return base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Waiting for jobs...");
            await Task.Run(() =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    FileModel currentFile = null;

                    if (FileQueue.TryDequeue(out currentFile))
                    {
                        _logger.LogDebug("Thread Id in execute : {0}", Thread.CurrentThread.ManagedThreadId);
                        Parallel.Invoke(
                            () =>
                            {
                                ProcessFile(currentFile);
                            });
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }

                }
            }, stoppingToken);
        }

        // INITIALIZE FILE SYSTEM WATCHER
        private void Watcher(string folderName)
        {
            try
            {
                var watcher = new FileSystemWatcher(folderName, "*.pdf")
                {
                    IncludeSubdirectories = false,
                    InternalBufferSize = 65536,
                    NotifyFilter = NotifyFilters.FileName
                };
                //Create event subscription for folder changes
                watcher.Created += OnFileCreated;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;

                //add wather to list of wathchers
                watchers.Add(watcher);
            }
            catch (Exception)
            {
                _logger.LogError("Folder not found...");
                throw;
            }
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            //  Show that an error has been detected.
            _logger.LogError("The FileSystemWatcher has detected an error");
            //  Give more information if the error is due to an internal buffer overflow.
            if (e.GetException().GetType() == typeof(InternalBufferOverflowException))
            {
                //  This can happen if Windows is reporting many file system events quickly
                //  and internal buffer of the  FileSystemWatcher is not large enough to handle this
                //  rate of events. The InternalBufferOverflowException error informs the application
                //  that some of the file system events are being lost.
                _logger.LogError("The file system watcher experienced an internal buffer overflow: {0}", e.GetException().Message);
            }
            _logger.LogError("***************Losed the watcher subscription*************");
        }

        //Event handler for folder
        private void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            Task task = Task.Run(() =>
            {
                Thread.Sleep(pdfCreationThreadWait);
                var processPath = e.FullPath.Replace("\\", "/");
                FileQueue.Enqueue(new FileModel { Name = e.Name, FullPath = processPath });
                _logger.LogDebug("Enqued file - {0}", e.Name);
            });
        }

        private void ProcessFile(FileModel file)
        {
            Task.Run(async () =>
            {
                try
                {
                    // get the printer
                    var path = Path.GetDirectoryName(file.FullPath).Replace("\\","/");

                    var printer = gatesPrinter.GetValueOrDefault(path);

                    var format = _configuration.GetSection(formatType).Value;
                    bool printed = await HasPrintedAsync(file.FullPath, printer, format);
                    format = "A4";
                    //bool printed =  HasPrinted(file.FullPath, printer, format);
                    if (printed)
                    {
                        // Rename File
                        RenamePrintedFile(file);
                        // move file for deletion in special folder
                        MovePrintedFile(file);
                    }
                    else
                    {
                        _logger.LogWarning("File: {0} not printed", file.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("{0}-{1}", ex.Message, ex.StackTrace);
                }
            });

        }

        private bool HasPrinted(string fullPath, string printer, string format)
        {
            try
            {
                using (Process compiler = new Process())
                {
                    compiler.StartInfo.FileName = printerAgentExecutable;
                    
                    var arg = string.Concat("\"",fullPath, "\" -print-to \"", printer, "\" -print-settings \"noscale ", format,"\"");
                    compiler.StartInfo.Arguments = arg;
                    compiler.StartInfo.UseShellExecute = false;
                    compiler.StartInfo.RedirectStandardOutput = true;
                    compiler.Start();

                    return compiler.WaitForExit(1000);
                }
            }
            catch (Exception)
            {
                throw;

            }
        }

        private async Task<bool> HasPrintedAsync(string fullPath, string printer, string format)
        {
            try
            {
                var hasSucceded = await Task.Run(() =>
                {
                    Process process = new Process();

                    ProcessStartInfo startInfo = new ProcessStartInfo(printerAgentExecutable);
                    
                    //var arg = string.Concat("\"", fullPath, "\" -print-to \"", printer, "\" -print-settings \"noscale paper=", format, "\"");
                    var arg = string.Concat("\"", fullPath, "\" -print-to \"", printer, "\" -print-settings \"noscale ","\"");

                    startInfo.Arguments = arg;
                    startInfo.WindowStyle = ProcessWindowStyle.Normal;
                    
                    process.StartInfo = startInfo;
                    var hasStarted = process.Start();

                    var hasExit = process.WaitForExit(10000);

                    if (hasExit)
                    {
                        _logger.LogInformation("File: {0} printed at printer: {1}", fullPath, printer);
                        return hasExit;
                    }

                    return hasExit;
                });
                return hasSucceded;
            }
            catch (Exception ex)
            {
                _logger.LogError("Process for printing has thrown exception. Error:{0}-{1}", ex.Message, ex.StackTrace);
                return false;
            }
        }

        private void RenamePrintedFile(FileModel e)
        {
            try
            {
                var dir = Path.GetDirectoryName(e.FullPath);
                var printedFile = e.Name + "_printed";
                var convertedPath = Path.Combine(dir, printedFile);

                File.Move(e.FullPath, convertedPath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning("{0}-{1}", ex.Message, ex.StackTrace);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("{0}-{1}", ex.Message, ex.StackTrace);
            }
        }

        private void MovePrintedFile(FileModel file)
        {
            try
            {
                var sourceFile = String.Concat(file.FullPath + "_printed");
                var destinationFile = String.Concat(Path.Combine(ToBeDeletedFolder, file.Name), "_printed");

                if (File.Exists(destinationFile))
                {
                    File.Delete(destinationFile);
                    _logger.LogDebug("File {0} already exist. Deleted old.", file.Name);
                }

                File.Move(sourceFile, destinationFile);
                _logger.LogDebug("File {0} moved", file.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("{0}-{1}", ex.Message, ex.StackTrace);
            }
        }
    }
}