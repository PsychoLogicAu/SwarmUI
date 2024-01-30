﻿using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticToolkit;
using Newtonsoft.Json.Linq;
using StableSwarmUI.Accounts;
using StableSwarmUI.Core;
using StableSwarmUI.Text2Image;
using StableSwarmUI.Utils;
using StableSwarmUI.WebAPI;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using static StableSwarmUI.Builtin_GridGeneratorExtension.GridGenCore;
using Image = StableSwarmUI.Utils.Image;
using ISImage = SixLabors.ImageSharp.Image;
using ISImageRGBA = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace StableSwarmUI.Builtin_GridGeneratorExtension;

/// <summary>Extension that adds a tool to generate grids of images.</summary>
public class GridGeneratorExtension : Extension
{
    public static T2IRegisteredParam<string> PromptReplaceParameter, PresetsParameter;

    public override void OnPreInit()
    {
        ScriptFiles.Add("Assets/grid_gen.js");
        StyleSheetFiles.Add("Assets/grid_gen.css");
        ASSETS_DIR = $"{FilePath}/Assets";
        EXTRA_FOOTER = $"Images area auto-generated by an AI (Stable Diffusion) and so may not have been reviewed by the page author before publishing.\n<script src=\"stableswarmui_gridgen_local.js?vary={Utilities.VaryID}\"></script>";
        EXTRA_ASSETS.Add("stableswarmui_gridgen_local.js");
        PromptReplaceParameter = T2IParamTypes.Register<string>(new("[Grid Gen] Prompt Replace", "Replace text in the prompt (or negative prompt) with some other text.",
            "", VisibleNormally: false, AlwaysRetain: true, Toggleable: true, ChangeWeight: -6, ParseList: (list) =>
            {
                if (list.Any(v => v.Contains('=')))
                {
                    return list;
                }
                string first = list[0];
                return list.Select(v =>
                {
                    bool skip = v.StartsWith("SKIP:");
                    if (skip)
                    {
                        v = v["SKIP:".Length..].Trim();
                        return $"SKIP:{first}={v}";
                    }
                    return $"{first}={v}";
                }).ToList();
            }));
        PresetsParameter = T2IParamTypes.Register<string>(new("[Grid Gen] Presets", "Apply parameter presets to the image. Can use a comma-separated list to apply multiple per-cell, eg 'a, b || a, c || b, c'",
            "", VisibleNormally: false, AlwaysRetain: true, Toggleable: true, ValidateValues: false, ChangeWeight: 2, GetValues: (session) => session.User.GetAllPresets().Select(p => p.Title).ToList()));
        GridCallInitHook = (call) =>
        {
            call.LocalData = new GridCallData();
        };
        GridCallParamAddHook = (call, param, val) =>
        {
            if (call.Grid.MinWidth == 0)
            {
                call.Grid.MinWidth = call.Grid.InitialParams.Get(T2IParamTypes.Width);
            }
            if (call.Grid.MinHeight == 0)
            {
                call.Grid.MinHeight = call.Grid.InitialParams.GetImageHeight();
            }
            string cleaned = T2IParamTypes.CleanTypeName(param);
            if (cleaned == "gridgenpromptreplace")
            {
                (call.LocalData as GridCallData).Replacements.Add(val);
                return true;
            }
            else if (cleaned == "width" || cleaned == "outwidth")
            {
                call.Grid.MinWidth = Math.Min(call.Grid.MinWidth, int.Parse(val));
            }
            else if (cleaned == "height" || cleaned == "outheight")
            {
                call.Grid.MinHeight = Math.Min(call.Grid.MinHeight, int.Parse(val));
            }
            return false;
        };
        GridCallApplyHook = (call, param, dry) =>
        {
            foreach (string replacement in (call.LocalData as GridCallData).Replacements)
            {
                string[] parts = replacement.Split('=', 2);
                string key = parts[0].Trim();
                string val = parts[1].Trim();
                foreach (string paramId in param.ValuesInput.Keys.Where(k => k.EndsWith("prompt") && param.ValuesInput[k] is string).ToArray())
                {
                    param.ValuesInput[paramId] = param.ValuesInput[paramId].ToString().Replace(key, val);
                }
            }
        };
        GridRunnerPreRunHook = (runner) =>
        {
            // TODO: Progress update
        };
        GridRunnerPreDryHook = (runner) =>
        {
            // Nothing to do.
        };
        GridRunnerPostDryHook = (runner, param, set) =>
        {
            param.NormalizeSeeds();
            StableSwarmUIGridData data = runner.Grid.LocalData as StableSwarmUIGridData;
            if (data.Claim.ShouldCancel)
            {
                Logs.Debug("Grid gen hook cancelling per user interrupt request.");
                runner.Grid.MustCancel = true;
                return Task.CompletedTask;
            }
            Task[] waitOn = data.GetActive();
            if (waitOn.Length > data.MaxSimul)
            {
                Task.WaitAny(waitOn);
            }
            if (Volatile.Read(ref data.ErrorOut) is not null && !data.ContinueOnError)
            {
                throw new InvalidOperationException("Errored");
            }
            void setError(string message)
            {
                Logs.Error($"Grid generator hit error: {message}");
                Volatile.Write(ref data.ErrorOut, new JObject() { ["error"] = message });
                data.Signal.Set();
            }
            T2IParamInput thisParams = param.Clone();
            if (thisParams.TryGet(PresetsParameter, out string presets))
            {
                List<T2IPreset> userPresets = data.Session.User.GetAllPresets();
                foreach (string preset in presets.ToLowerFast().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    T2IPreset match = userPresets.FirstOrDefault(p => p.Title.ToLowerFast() == preset);
                    if (match is null)
                    {
                        setError($"Could not find preset '{preset}'");
                        return Task.CompletedTask;
                    }
                    match.ApplyTo(thisParams);
                }
            }
            thisParams.Set(T2IParamTypes.NoPreviews, true);
            int iteration = runner.Iteration;
            Task t = Task.Run(() => T2IEngine.CreateImageTask(thisParams, $"{iteration}", data.Claim, data.AddOutput, setError, true, Program.ServerSettings.Backends.PerRequestTimeoutMinutes,
                (image, metadata) =>
                {
                    Logs.Info($"Completed gen #{iteration} (of {runner.TotalRun}) ... Set: '{set.Data}', file '{set.BaseFilepath}'");
                    string mainpath = $"{set.Grid.Runner.BasePath}/{set.BaseFilepath}";
                    string ext = set.Grid.Format;
                    string metaExtra = "";
                    if (image.Img.Type != Image.ImageType.IMAGE)
                    {
                        ext = image.Img.Extension;
                        metaExtra += $"file_extensions_alt[\"{set.BaseFilepath}\"] = \"{ext}\"\nfix_video(\"{set.BaseFilepath}\")";
                    }
                    string targetPath = $"{mainpath}.{ext}";
                    string dir = targetPath.Replace('\\', '/').BeforeLast('/');
                    if (set.Grid.OutputType == Grid.OutputyTypeEnum.WEB_PAGE)
                    {
                        if (!Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        File.WriteAllBytes(targetPath, image.Img.ImageData);
                        if (set.Grid.PublishMetadata && (!string.IsNullOrWhiteSpace(metadata) || !string.IsNullOrWhiteSpace(metaExtra)))
                        {
                            metadata ??= "{}";
                            File.WriteAllBytes($"{mainpath}.metadata.js", $"all_metadata[\"{set.BaseFilepath}\"] = {metadata}\n{metaExtra}".EncodeUTF8());
                        }
                        data.AddOutput(new JObject() { ["image"] = $"/{set.Grid.Runner.URLBase}/{set.BaseFilepath}.{ext}", ["metadata"] = metadata });
                    }
                    else
                    {
                        (string url, string filePath) = thisParams.Get(T2IParamTypes.DoNotSave, false) ? (data.Session.GetImageB64(image.Img), null) : data.Session.SaveImage(image.Img, iteration, thisParams, metadata);
                        if (url == "ERROR")
                        {
                            setError($"Server failed to save an image.");
                            return;
                        }
                        data.AddOutput(new JObject() { ["image"] = url, ["batch_index"] = $"{iteration}", ["metadata"] = string.IsNullOrWhiteSpace(metadata) ? null : metadata });
                        if (set.Grid.OutputType == Grid.OutputyTypeEnum.GRID_IMAGE)
                        {
                            data.GeneratedOutputs.TryAdd(set.BaseFilepath, image.Img);
                        }
                    }
                }));
            lock (data.UpdateLock)
            {
                data.Rendering.Add(t);
            }
            int requests = Program.Backends.QueuedRequests;
            if (requests < Program.ServerSettings.Backends.MaxRequestsForcedOrder)
            {
                Logs.Debug($"Grid Gen micro-pausing to maintain order as {requests} < {Program.ServerSettings.Backends.MaxRequestsForcedOrder}");
                Task.Delay(20).Wait(); // Tiny few-ms delay to encourage tasks retaining order.
            }
            return t;
        };
        PostPreprocessCallback = (grid) =>
        {
            StableSwarmUIGridData data = grid.Grid.LocalData as StableSwarmUIGridData;
            data.Claim.Extend(grid.TotalRun, 0, 0, 0);
            data.AddOutput(BasicAPIFeatures.GetCurrentStatusRaw(data.Session));
        };
    }

    public override void OnInit()
    {
        API.RegisterAPICall(GridGenRun);
        API.RegisterAPICall(GridGenDoesExist);
        API.RegisterAPICall(GridGenSaveData);
        API.RegisterAPICall(GridGenDeleteData);
        API.RegisterAPICall(GridGenGetData);
        API.RegisterAPICall(GridGenListData);
    }

    public async Task<JObject> GridGenSaveData(Session session, string gridName, JObject rawData)
    {
        session.User.SaveGenericData("gridgenerator", gridName, rawData["data"].ToString());
        return new JObject() { ["success"] = true };
    }

    public async Task<JObject> GridGenDeleteData(Session session, string gridName)
    {
        session.User.DeleteGenericData("gridgenerator", gridName);
        return new JObject() { ["success"] = true };
    }

    public async Task<JObject> GridGenGetData(Session session, string gridName)
    {
        string data = session.User.GetGenericData("gridgenerator", gridName);
        if (data is null)
        {
            return new() { ["error"] = "Could not find that Grid Generator save." };
        }
        return new JObject() { ["data"] = data.ParseToJson() };
    }

    public async Task<JObject> GridGenListData(Session session)
    {
        List<string> data = session.User.ListAllGenericData("gridgenerator");
        return new JObject() { ["data"] = JArray.FromObject(data.ToArray()) };
    }

    public class GridCallData
    {
        public List<string> Replacements = new();
    }

    public class StableSwarmUIGridData
    {
        public List<Task> Rendering = new();

        public LockObject UpdateLock = new();

        public ConcurrentQueue<JObject> Generated = new();

        public Session Session;

        public int MaxSimul;

        public Session.GenClaim Claim;

        public JObject ErrorOut;

        public AsyncAutoResetEvent Signal = new(false);

        public ConcurrentDictionary<string, Image> GeneratedOutputs = new();

        public bool ContinueOnError = false;

        public Task[] GetActive()
        {
            lock (UpdateLock)
            {
                return Rendering.Where(x => !x.IsCompleted).ToArray();
            }
        }

        public void AddOutput(JObject obj)
        {
            Generated.Enqueue(obj);
            Signal.Set();
        }
    }

    public static JObject ExToError(Exception ex)
    {
        if (ex is AggregateException && ex.InnerException is AggregateException)
        {
            ex = ex.InnerException;
        }
        if (ex is AggregateException && ex.InnerException is InvalidDataException)
        {
            ex = ex.InnerException;
        }
        if (ex is InvalidDataException)
        {
            return new JObject() { ["error"] = $"Failed due to: {ex.Message}" };
        }
        else
        {
            Logs.Error($"Grid Generator hit error: {ex}");
            return new JObject() { ["error"] = "Failed due to internal error." };
        }
    }

    public string CleanFolderName(string name)
    {
        name = Utilities.StrictFilenameClean(name);
        if (name.Trim() == "")
        {
            throw new InvalidDataException("Output folder name cannot be empty.");
        }
        return $"Grids/{name.Trim()}";
    }

    public async Task<JObject> GridGenDoesExist(Session session, string folderName)
    {
        folderName = CleanFolderName(folderName);
        bool exists = File.Exists($"{session.User.OutputDirectory}/{folderName}/index.html");
        return new JObject() { ["exists"] = exists };
    }

    public static FontCollection MainFontCollection;
    public static ConcurrentDictionary<float, Font> Fonts = new();

    public static Font GetFont(float sizeMult)
    {
        if (MainFontCollection is null)
        {
            MainFontCollection = new();
            MainFontCollection.Add("src/wwwroot/fonts/unifont-12.0.01.woff2");
        }
        return Fonts.GetOrCreate(sizeMult, () => MainFontCollection.Families.First().CreateFont(16 * sizeMult, FontStyle.Bold));
    }

    public async Task<JObject> GridGenRun(WebSocket socket, Session session, JObject raw, string outputFolderName, bool doOverwrite, bool fastSkip, bool generatePage, bool publishGenMetadata, bool dryRun, bool weightOrder, string outputType, bool continueOnError)
    {
        using Session.GenClaim claim = session.Claim(gens: 1);
        T2IParamInput baseParams;
        try
        {
            baseParams = T2IAPI.RequestToParams(session, raw["baseParams"] as JObject);
            outputFolderName = CleanFolderName(outputFolderName);
        }
        catch (InvalidDataException ex)
        {
            await socket.SendJson(new JObject() { ["error"] = ex.Message }, API.WebsocketTimeout);
            return null;
        }
        async Task sendStatus()
        {
            await socket.SendJson(BasicAPIFeatures.GetCurrentStatusRaw(session), API.WebsocketTimeout);
        }
        baseParams.Remove(T2IParamTypes.BatchSize);
        baseParams.Remove(T2IParamTypes.Images);
        baseParams.Remove(T2IParamTypes.SaveIntermediateImages);
        await sendStatus();
        StableSwarmUIGridData data = new() { Session = session, Claim = claim, MaxSimul = session.User.Restrictions.CalcMaxT2ISimultaneous, ContinueOnError = continueOnError };
        Grid grid = null;
        try
        {
            string ext = Image.ImageFormatToExtension(session.User.Settings.FileFormat.ImageFormat);
            string urlBase = Program.ServerSettings.Paths.AppendUserNameToOutputPath ? $"View/{session.User.UserID}" : "Output";
            Task mainRun = Task.Run(() => grid = Run(baseParams, raw["gridAxes"], data, null, session.User.OutputDirectory, urlBase, outputFolderName, doOverwrite, fastSkip, generatePage, publishGenMetadata, dryRun, weightOrder, outputType, ext));
            while (!mainRun.IsCompleted || data.GetActive().Any() || data.Generated.Any())
            {
                await data.Signal.WaitAsync(TimeSpan.FromSeconds(1));
                Program.GlobalProgramCancel.ThrowIfCancellationRequested();
                while (data.Generated.TryDequeue(out JObject toSend))
                {
                    await socket.SendJson(toSend, API.WebsocketTimeout);
                }
            }
            if (mainRun.IsFaulted)
            {
                throw mainRun.Exception;
            }
            if (grid.OutputType == Grid.OutputyTypeEnum.GRID_IMAGE && grid.Axes.Count <= 3)
            {
                (string, string) proc(AxisValue val) => (val.Title, T2IParamTypes.CleanNameGeneric(val.Key));
                List<(string, string)> xAxis = grid.Axes[0].Values.Where(v => !v.Skip).Select(proc).ToList();
                List<(string, string)> yAxis = grid.Axes.Count > 1 ? grid.Axes[1].Values.Where(v => !v.Skip).Select(proc).ToList() : new() { (null, null) };
                List<(string, string)> y2Axis = grid.Axes.Count > 2 ? grid.Axes[2].Values.Where(v => !v.Skip).Select(proc).ToList() : new() { (null, null) };
                int maxWidth = data.GeneratedOutputs.Max(x => x.Value.ToIS.Width);
                int maxHeight = data.GeneratedOutputs.Max(x => x.Value.ToIS.Height);
                Font font = GetFont(1);
                TextOptions options = new(font);
                FontRectangle rect = TextMeasurer.MeasureSize("ABCdefg Word Prefix", options);
                int textWidth = (int)Math.Ceiling(rect.Width);
                int rawTextHeight = (int)Math.Ceiling(rect.Height);
                int textHeight = rawTextHeight * 2;
                int totalWidth = maxWidth * xAxis.Count + textWidth;
                int totalHeight = maxHeight * (yAxis.Count * y2Axis.Count) + textHeight * y2Axis.Count;
                Logs.Info($"Will generate grid image of size {totalWidth}x{totalHeight}");
                ISImageRGBA gridImg = new(totalWidth, totalHeight);
                gridImg.Mutate(m =>
                {
                    Brush brush = new SolidBrush(Color.Black);
                    void DrawTextAutoScale(string text, float x, float y, float width, float height)
                    {
                        RichTextOptions rto = new(font) { WrappingLength = width, Origin = new(x, y) };
                        float lines = height / rawTextHeight;
                        FontRectangle measured = TextMeasurer.MeasureSize(text, options);
                        Logs.Debug($"Measured {text} as {measured.Width}x{measured.Height} in {width}x{height} with {lines} lines");
                        if (measured.Width < width * lines * 0.5)
                        {
                            rto.Font = GetFont(2);
                        }
                        else if (measured.Width > width * lines * 2)
                        {
                            rto.Font = GetFont(0.5f);
                        }
                        else if (measured.Width > width * lines)
                        {
                            rto.Font = GetFont(0.75f);
                        }
                        m.DrawText(rto, text, brush);
                    }
                    m.Fill(Color.White);
                    int xIndex = 0;
                    float yIndex = 0;
                    foreach ((string x, _) in xAxis)
                    {
                        DrawTextAutoScale(x, xIndex * maxWidth + textWidth, 0, maxWidth, textHeight);
                        xIndex++;
                    }
                    foreach ((string y2, _) in y2Axis)
                    {
                        if (y2 is not null)
                        {
                            DrawTextAutoScale(y2, 0, yIndex * maxHeight + textHeight, textWidth, maxHeight);
                            yIndex += textHeight / (float)maxHeight;
                        }
                        foreach ((string y, _) in yAxis)
                        {
                            if (y is not null)
                            {
                                DrawTextAutoScale(y, 0, yIndex * maxHeight + textHeight * 2, textWidth, maxHeight);
                            }
                            yIndex++;
                        }
                    }
                    yIndex = 0;
                    foreach ((_, string y2) in y2Axis)
                    {
                        if (y2 is not null)
                        {
                            yIndex += textHeight / (float)maxHeight;
                        }
                        foreach ((_, string y) in yAxis)
                        {
                            xIndex = 0;
                            foreach ((_, string x) in xAxis)
                            {
                                string imgPath = x;
                                if (y is not null)
                                {
                                    imgPath = $"{imgPath}/{y}";
                                    if (y2 is not null)
                                    {
                                        imgPath = $"{imgPath}/{y2}";
                                    }
                                }
                                ISImage img = data.GeneratedOutputs[imgPath].ToIS;
                                m.DrawImage(img, new Point(xIndex * maxWidth + textWidth, (int)(yIndex * maxHeight + textHeight)), 1);
                                xIndex++;
                            }
                            yIndex++;
                        }
                    }
                });
                Logs.Info("Generated, saving...");
                Image outImg = new(gridImg);
                int batchId = xAxis.Count * yAxis.Count * y2Axis.Count;
                Logs.Debug("Apply metadata...");
                (outImg, string metadata) = session.ApplyMetadata(outImg, grid.InitialParams, batchId);
                Logs.Debug("Metadata applied, save to file...");
                (string url, string filePath) = grid.InitialParams.Get(T2IParamTypes.DoNotSave, false) ? (data.Session.GetImageB64(outImg), null) : data.Session.SaveImage(outImg, batchId, grid.InitialParams, metadata);
                if (url == "ERROR")
                {
                    data.ErrorOut = new JObject() { ["error"] = $"Server failed to save an image." };
                    throw new InvalidOperationException();
                }
                Logs.Debug("Saved to file, send over websocket...");
                await socket.SendJson(new JObject() { ["image"] = url, ["batch_index"] = $"{batchId}", ["metadata"] = string.IsNullOrWhiteSpace(metadata) ? null : metadata }, API.WebsocketTimeout);
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref data.ErrorOut) is null)
            {
                Volatile.Write(ref data.ErrorOut, ExToError(ex));
            }
        }
        if (grid is not null && grid.OutputType == Grid.OutputyTypeEnum.WEB_PAGE)
        {
            PostClean(session.User.OutputDirectory, outputFolderName);
        }
        Task faulted = data.Rendering.FirstOrDefault(t => t.IsFaulted);
        JObject err = Volatile.Read(ref data.ErrorOut);
        if (faulted is not null && err is null)
        {
            err = ExToError(faulted.Exception);
        }
        if (err is not null)
        {
            Logs.Error($"GridGen stopped while running: {err}");
            await socket.SendJson(err, TimeSpan.FromMinutes(1));
            return null;
        }
        Logs.Info("Grid Generator completed successfully");
        claim.Complete(gens: 1);
        claim.Dispose();
        await sendStatus();
        await socket.SendJson(new JObject() { ["success"] = "complete" }, API.WebsocketTimeout);
        return null;
    }
}
