﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DMT.Attributes;
using DMT.Compiler;
using DMT;
using Mono.Cecil;

namespace DMT
{
    public class PatchData
    {

        public const string AssemblyFilename = "Assembly-CSharp.dll";
        public static string BuildId { get; set; }

        public string UpdateSource { get; set; } = String.Empty;
        public string UpdateDestination { get; set; } = String.Empty;
        public bool IsUpdate
        {
            get { return !String.IsNullOrEmpty(UpdateSource) && !String.IsNullOrEmpty(UpdateDestination); }
        }

        public string ModFolder { get; set; }
        public string GameFolder { get; set; }

        public bool IsDedicatedServer
        {
            get { return Directory.Exists(GameFolder + "7DaysToDieServer_Data"); }
        }

        public string StartPath
        {
            get { return GameFolder + (IsDedicatedServer ? "startdedicated.bat" : "7DaysToDie.exe"); }
        }
        
        public string ExePath
        {
            get { return GameFolder + (IsDedicatedServer ? "7DaysToDieServer.exe" : "7DaysToDie.exe"); }
        }

        public string ConfigFolder { get; set; }
        public string ManagedFolder { get; set; }

        public string GameDllLocation { get; set; }
        public string ModDllLocation { get; set; }

        public RunSection RunSection { get; set; } = RunSection.InitialPatch;

        public string BackupFolder { get; set; }

        public ICompiler Compiler { get; set;  } = new CodeDomCompiler();

        public string BuildFolder { get; set; }
        public string BackupDllLocataion { get; set; }
        public string InitialDllLocation { get; set; }
        public string LinkedDllLocation { get; set; }
        
        public string ModsDllTempLocation { get; set; }
        

        public List<IPatchTask> PatchTasks { get; set; } = new List<IPatchTask>();

        public List<ModInfo> Mods
        {
            get { return BuildSettings.Instance.Mods; }
        }

        public IList<ModInfo> ActiveMods
        {
            get { return Mods.Where(d => d.Enabled).ToList(); }
        }
        public IList<ModInfo> InactiveMods
        {
            get { return Mods.Where(d => d.Enabled == false).ToList(); }
        }

        public static PatchData Create(BuildSettings settings)
        {
            return Create(settings.BackupFolder, settings.ModFolder, String.Empty, settings.Compiler);
        }

        public static PatchData Create(string backupFolder, string modFolder, string gameFolder, ICompiler compiler)
        {

            var ret = new PatchData
            {
                BackupFolder = backupFolder.FolderFormat(),
                ModFolder = modFolder.FolderFormat(),
                GameFolder = gameFolder.FolderFormat(),
                Compiler = compiler,
            };

            return ret;

        }

        public void Init()
        {

            if (BuildId == null)
            {
                BuildId = Guid.NewGuid().ToString(); 
            }

            BuildFolder = Path.GetTempPath() + "7DTD-DMT/" + BuildId + "/";
            Helper.MakeFolder(BuildFolder);
            
            
            BackupFolder = Application.StartupPath + "/Backups/"; 
            InitialDllLocation = BuildFolder + "InitialPatch.dll";
            LinkedDllLocation = BuildFolder + "LinkedPatch.dll";
            ModsDllTempLocation = BuildFolder + "Mods.dll";

            ConfigFolder = GameFolder + "Data/Config/";
            ManagedFolder = GameFolder + (IsDedicatedServer ? "7DaysToDieServer_Data" : "7DaysToDie_Data") + "/Managed/";
            ModDllLocation = ManagedFolder + "Mods.dll";
            GameDllLocation = ManagedFolder + AssemblyFilename;

        }


        public ModuleDefinition ReadModuleDefinition(string path)
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(ManagedFolder);

            ModuleDefinition module = ModuleDefinition.ReadModule(path, new ReaderParameters() { AssemblyResolver = resolver, });
            return module;

        }

        public void ParseArguments(string[] args)
        {

            Logging.Log("Parsing arguments");

            var cmdLineArgs = AppDomain.CurrentDomain.GetAssemblies().SelectMany(d => d.GetInterfaceImplementers<ICommandLineArgument>()).ToList();

            for (int x = 0; x < args.Length; x++)
            {
                var a = args[x];
                if (a == null) continue;
                var next = args.GetNext(x);

                foreach (var c in cmdLineArgs)
                {
                    if (c.Apply(a, next, this))
                    {
                        cmdLineArgs.Remove(c);
                        break;
                    }
                }
            }

            Init();
        }

        public int Patch()
        {

            var patchTaskType = typeof(IPatchTask);

            var asses = AppDomain.CurrentDomain.GetAssemblies().ToList();

            asses.RemoveAll(d=> BuildSettings.IsLocalBuild && (d.FullName.Contains("Mods")) || d.FullName.Contains("tmp"));

            if (BuildSettings.IsLocalBuild && (asses.Any(d => d.FullName.Contains("Mods")) || asses.Any(d => d.FullName.Contains("tmp"))))
            {
                var acs = BackupFolder + "Assembly-CSharp.dll";
                //File.Copy(acs, "ACSback.dll", true);
                
                var unityFiles = Directory.GetFiles(BackupFolder, "*.dll");

                foreach(var f in unityFiles)
                {

                    if (f.Contains("System."))
                        continue;
                    if (f.Contains(".UI."))
                        continue;
                    var info = new FileInfo(f);
                    var name = Path.GetFileNameWithoutExtension(info.Name);
                    if (asses.Any(d => d.FullName.Contains(name)))
                        continue;
                    asses.Add(System.Reflection.Assembly.LoadFrom(f));
                }
                asses.Add(System.Reflection.Assembly.LoadFrom(acs));

                AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

                var assemblyPath = Path.GetDirectoryName(Path.ChangeExtension(Path.GetTempFileName(), "dll"));
                File.Copy(acs, assemblyPath + "/" + Path.GetFileName(acs), true);
                try
                {
                    File.Copy(acs, @"D:/!Projects/DMT/DMTViewer/bin/Debug/" + Path.GetFileName(acs), true);
                }
                catch (Exception e)
                {

                }
            }

            var patches = asses
                .SelectMany(s => s.GetTypes())
                .Where(d => patchTaskType.IsAssignableFrom(d) && !d.IsInterface && !d.IsAbstract
                            && ((int?)d.GetCustomAttributes(true).OfType<MinRun>().FirstOrDefault()?.Section ?? (int)RunSection.FinalPatch) == (int)RunSection
                )
                .OrderBy(d => d.GetCustomAttributes(true).OfType<MinRun>().FirstOrDefault()?.Value ?? int.MaxValue)
                .Select(d => Activator.CreateInstance(d) as IPatchTask).ToList();

            //Logging.LogError("Got asses");
            //foreach (var t in patches)
            //{
            //    t.PrePatch(data);
            //}
            foreach (var t in patches)
            {

                var log = "Running " + t.GetType().Name;
                Logging.Log(log);

                if (t?.Patch(this) == false)
                {
                    Logging.LogError("Build failed");
                    BuildSettings.AutoBuildComplete = true;
                    return -1;
                }
            }


            if (patches.Any(d => ((int?) d.GetType().GetCustomAttributes(true).OfType<MinRun>().FirstOrDefault()?.Section ?? (int) RunSection.InitialPatch) == (int) RunSection.FinalPatch))
            {
                Logging.Log("Build Complete");
            }

            //foreach (var t in patches)
            //{
            //    t.PostPatch(data);
            //}
            return 0;

        }

        private System.Reflection.Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            string filename = args.Name.Split(',')[0] + ".dll".ToLower();

            var gameLocation = Path.GetDirectoryName(GameDllLocation);
            string asmFile = gameLocation + "/"+ filename;

            try
            {
                return System.Reflection.Assembly.LoadFrom(asmFile);
            }
            catch (Exception ex)
            {
                return null;
            }

        }

        public IEnumerable<string> FindFiles(string filter)
        {

            var ret = new List<string>();
            foreach (var m in ActiveMods)
            {
                string file = $"{m.Location.FolderFormat()}{filter}";
                if (File.Exists(file))
                {
                    ret.Add(file);
                }
            }

            return ret;
        }

        public IList<string> FindFiles(string folder, string filter, bool recursive)
        {

            var ret = new List<string>();
            foreach (var m in ActiveMods)
            {
                ret.AddRange(m.FindFiles(folder, filter, recursive));
            }

            return ret;
        }

        public IList<ModInfo> GetHarmonyMods()
        {

            var ret = new List<ModInfo>();
            foreach (var m in ActiveMods)
            {
                if (m.FindFiles("Harmony", "*.cs", true).Any())
                    ret.Add(m);
            }

            return ret;
        }



    }
}
