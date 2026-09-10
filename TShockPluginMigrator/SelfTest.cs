using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RefTile.PluginMigrator;

/// <summary>
/// Self test: builds a stub "OTAPI" assembly containing the classic ITile/Tile model,
/// compiles a small plugin against it, runs the migrator, then verifies the output no
/// longer references ITile/Tile and does reference TileData.
/// </summary>
public static class SelfTest
{
    public static bool Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "otapi-migrator-selftest");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        try
        {
            string? csc = FindCsc();
            if (csc is null) { Console.WriteLine("[FAIL] could not locate csc.dll"); return false; }

            string stubCs = Path.Combine(dir, "stub.cs");
            string pluginCs = Path.Combine(dir, "plugin.cs");
            string stubDll = Path.Combine(dir, "OTAPI.dll");
            string pluginDll = Path.Combine(dir, "MyPlugin.dll");
            string outDll = Path.Combine(dir, "out", "MyPlugin.dll");

            File.WriteAllText(stubCs, StubSource);
            File.WriteAllText(pluginCs, PluginSource);

            var tpa = ((AppDomain.CurrentDomain.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
                .Split(Path.PathSeparator).Where(x => x.Length > 0).ToArray();

            if (!Compile(csc, dir, stubCs, stubDll, tpa)) return false;
            if (!Compile(csc, dir, pluginCs, pluginDll, tpa.Append(stubDll))) return false;

            Directory.CreateDirectory(Path.Combine(dir, "out"));
            Console.WriteLine($"migrating {pluginDll} ...");
            Migrator.Migrate(pluginDll, Path.Combine(dir, "out"));

            var problems = Verify(outDll);
            if (problems.Count == 0)
            {
                Console.WriteLine("[PASS] self-test OK - output assembly references Terraria.TileData, no ITile/Tile remain.");
                return true;
            }
            Console.WriteLine("[FAIL] verification problems:");
            foreach (var p in problems) Console.WriteLine("  - " + p);
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[FAIL] " + ex);
            return false;
        }
    }

    static bool Compile(string csc, string workDir, string source, string output, IEnumerable<string> refPaths)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(csc);
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-target:library");
        psi.ArgumentList.Add("-out:" + output);
        psi.ArgumentList.Add("-langversion:9.0");
        foreach (var r in refPaths)
            psi.ArgumentList.Add("-r:" + r);
        psi.ArgumentList.Add(source);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 || !File.Exists(output))
        {
            Console.WriteLine($"[FAIL] compile {Path.GetFileName(source)}: {stdout}{stderr}");
            return false;
        }
        return true;
    }

    static string? FindCsc()
    {
        var sdk = new DirectoryInfo(@"C:\Program Files\dotnet\sdk").GetDirectories()
            .OrderByDescending(d => d.Name).FirstOrDefault();
        if (sdk is null) return null;
        var csc = Path.Combine(sdk.FullName, "Roslyn", "bincore", "csc.dll");
        return File.Exists(csc) ? csc : null;
    }

    static List<string> Verify(string outDll)
    {
        var problems = new List<string>();
        if (!File.Exists(outDll)) { problems.Add("output dll missing"); return problems; }

        using var module = ModuleDefinition.ReadModule(outDll, new ReaderParameters { InMemory = true });
        int tileDataRefs = 0;
        string? tileDataScope = null;
        bool tileDataNotValueType = false;
        int ldloca = 0, stfld = 0, ldobj = 0, ldsfldNull = 0;
        int box = 0, unbox = 0, badUnbox = 0;
        int badNullInit = 0;

        foreach (var type in module.GetAllTypes())
        {
            foreach (var f in type.Fields) CheckType(f.FieldType, problems, ref tileDataRefs, ref tileDataScope, ref tileDataNotValueType);
            foreach (var p in type.Properties) CheckType(p.PropertyType, problems, ref tileDataRefs, ref tileDataScope, ref tileDataNotValueType);
            foreach (var m in type.Methods)
            {
                CheckType(m.ReturnType, problems, ref tileDataRefs, ref tileDataScope, ref tileDataNotValueType);
                foreach (var p in m.Parameters) CheckType(p.ParameterType, problems, ref tileDataRefs, ref tileDataScope, ref tileDataNotValueType);
                if (!m.HasBody) continue;
                foreach (var v in m.Body.Variables) CheckType(v.VariableType, problems, ref tileDataRefs, ref tileDataScope, ref tileDataNotValueType);

                foreach (var inst in m.Body.Instructions)
                {
                    switch (inst.Operand)
                    {
                        case TypeReference tr: CheckType(tr, problems, ref tileDataRefs, ref tileDataScope, ref tileDataNotValueType); break;
                        case MethodReference mr:
                            if (Migrator.IsOldTile(mr.DeclaringType.FullName))
                                problems.Add($"method ref still on old tile: {mr.FullName}");
                            CheckType(mr.ReturnType, problems, ref tileDataRefs, ref tileDataScope, ref tileDataNotValueType);
                            foreach (var p in mr.Parameters) CheckType(p.ParameterType, problems, ref tileDataRefs, ref tileDataScope, ref tileDataNotValueType);
                            break;
                        case FieldReference fr:
                            if (Migrator.IsOldTile(fr.DeclaringType.FullName))
                                problems.Add($"field ref still on old tile: {fr.FullName}");
                            break;
                    }

                    // IL-level assertions for the struct rewrite
                    if (inst.OpCode == OpCodes.Ldloca || inst.OpCode == OpCodes.Ldloca_S) ldloca++;
                    if (inst.OpCode == OpCodes.Stfld && inst.Operand is FieldReference sf && sf.DeclaringType.FullName == Migrator.NewTile) stfld++;
                    if (inst.OpCode == OpCodes.Ldobj && inst.Operand is TypeReference lo && lo.FullName == Migrator.NewTile) ldobj++;
                    if (inst.OpCode == OpCodes.Ldsfld && inst.Operand is FieldReference nf && nf.Name == "NULL" && nf.DeclaringType.FullName == Migrator.NewTile) ldsfldNull++;
                    if (inst.OpCode == OpCodes.Box && inst.Operand is TypeReference bx && bx.FullName == Migrator.NewTile) box++;
                    if (inst.OpCode == OpCodes.Unbox_Any && inst.Operand is TypeReference ux && ux.FullName == Migrator.NewTile)
                    {
                        unbox++;
                        // dangerous (unfixed): a raw TileData value directly feeding unbox.any
                        var p = inst.Previous;
                        if (p is not null && p.OpCode == OpCodes.Call && p.Operand is MethodReference pm
                            && pm.Name == "New" && pm.DeclaringType.FullName == Migrator.NewTile)
                            badUnbox++;
                    }
                    // "ldnull" straight into a TileData local is invalid for a struct
                    if (inst.OpCode == OpCodes.Ldnull)
                    {
                        var nxt = inst.Next;
                        if (nxt is not null && (nxt.OpCode.Code is Mono.Cecil.Cil.Code.Stloc_0 or Mono.Cecil.Cil.Code.Stloc_1 or Mono.Cecil.Cil.Code.Stloc_2 or Mono.Cecil.Cil.Code.Stloc_3 or Mono.Cecil.Cil.Code.Stloc or Mono.Cecil.Cil.Code.Stloc_S))
                        {
                            VariableDefinition v = nxt.OpCode.Code switch
                            {
                                Mono.Cecil.Cil.Code.Stloc_0 => m.Body.Variables[0],
                                Mono.Cecil.Cil.Code.Stloc_1 => m.Body.Variables[1],
                                Mono.Cecil.Cil.Code.Stloc_2 => m.Body.Variables[2],
                                Mono.Cecil.Cil.Code.Stloc_3 => m.Body.Variables[3],
                                _ => (VariableDefinition)nxt.Operand!,
                            };
                            if (v.VariableType.FullName == Migrator.NewTile) badNullInit++;
                        }
                    }
                }
            }
        }

        if (tileDataRefs == 0) problems.Add("no Terraria.TileData references found");
        if (tileDataScope is not null && tileDataScope is "System.Runtime" or "System.Private.CoreLib" or "mscorlib" or "netstandard")
            problems.Add($"TileData scoped to the wrong assembly: {tileDataScope}");
        if (tileDataScope is not null && tileDataScope != "OTAPI")
            problems.Add($"TileData scope is '{tileDataScope}' (expected the referenced OTAPI assembly)");
        if (tileDataNotValueType) problems.Add("TileData reference does not carry the valuetype flag");
        if (ldloca == 0) problems.Add("no ldloca produced (instance-address fixes missing)");
        if (stfld == 0) problems.Add("no stfld on TileData produced (accessor rewrite missing)");
        if (ldobj == 0) problems.Add("no ldobj TileData produced (value load from get_Item missing)");
        if (ldsfldNull == 0) problems.Add("no ldsfld TileData::NULL produced (null handling missing)");
        if (badNullInit > 0) problems.Add($"{badNullInit} ldnull->TileData local init left behind");
        if (badUnbox > 0) problems.Add($"{badUnbox} unbox.any TileData directly fed by a raw TileData value (memory corruption risk)");
        return problems;
    }

    static void CheckType(TypeReference t, List<string> problems, ref int tileData, ref string? tileDataScope, ref bool tileDataNotValueType)
    {
        switch (t)
        {
            case ArrayType arr: CheckType(arr.ElementType, problems, ref tileData, ref tileDataScope, ref tileDataNotValueType); break;
            case ByReferenceType br: CheckType(br.ElementType, problems, ref tileData, ref tileDataScope, ref tileDataNotValueType); break;
            case GenericInstanceType git:
                foreach (var a in git.GenericArguments) CheckType(a, problems, ref tileData, ref tileDataScope, ref tileDataNotValueType);
                break;
            default:
                if (Migrator.IsOldTile(t.FullName))
                    problems.Add($"type still references {t.FullName}");
                if (t.FullName == Migrator.NewTile)
                {
                    tileData++;
                    tileDataScope ??= t.Scope?.Name;
                    if (!t.IsValueType) tileDataNotValueType = true;
                }
                break;
        }
    }

    const string StubSource = """
        namespace Terraria
        {
            public interface ITile
            {
                ushort type { get; set; }
                ushort wall { get; set; }
                bool active();
                void active(bool active);
                byte slope();
                void slope(byte slope);
                object Clone();
            }
            public class Tile : ITile
            {
                public ushort type { get; set; }
                public ushort wall { get; set; }
                public bool active() => true;
                public void active(bool active) { }
                public byte slope() => 0;
                public void slope(byte slope) { }
                public object Clone() => new Tile { type = type };
            }
            public static class Main
            {
                public static ITile[,] tile = new ITile[100, 100];
            }
        }
        """;

    const string PluginSource = """
        using Terraria;

        public class MyPlugin
        {
            public void Touch()
            {
                Main.tile[10, 20].type = 5;
                Main.tile[10, 20].active(true);
                bool b = Main.tile[5, 6].active();
                ITile t = Main.tile[7, 8];
                t.type = 9;
                t.wall = 1;
                byte s = t.slope();
                t.slope(3);
                ITile n = null;
                if (n == null)
                    n = new Tile();
                object o = t.Clone();
                ITile t2 = t != null ? (ITile)t.Clone() : new Tile();
                Take(t);
            }

            public void Take(ITile tile)
            {
                tile.active(false);
            }
        }
        """;
}
