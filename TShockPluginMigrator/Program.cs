using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RefTile.PluginMigrator;

public static class Program
{
    /// <summary>
    /// Patches the MonoMod.dll we ship so it behaves on NET9:
    ///  - get_GACPaths returns an empty array (GAC IL is never used);
    ///  - MatchingConditionals always returns true;
    ///  - get_WriterParameters uses a sane deterministic value instead of the broken current value.
    /// (Same patch as OTAPI.Scripts/Mods/PatchMonoMod.Server.cs.)
    /// </summary>
    public static void PatchMonoMod()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "MonoMod.dll");
        if (!File.Exists(dllPath)) return;
        var bin = File.ReadAllBytes(dllPath);
        using MemoryStream ms = new(bin);
        var asm = AssemblyDefinition.ReadAssembly(ms);
        var modder = asm.MainModule.Types.Single(x => x.FullName == "MonoMod.MonoModder");
        var gacPaths = modder.Methods.Single(m => m.Name == "get_GACPaths");
        var il = gacPaths.Body.GetILProcessor();
        if (il.Body.Instructions.Count != 3)
        {
            il.Body.Instructions.Clear();
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Newarr, asm.MainModule.ImportReference(typeof(string)));
            il.Emit(OpCodes.Ret);

            var mc = modder.Methods.Single(m => m.Name == "MatchingConditionals" && m.Parameters.Count == 2 && m.Parameters[1].ParameterType.Name == "AssemblyNameReference");
            il = mc.Body.GetILProcessor();
            mc.Body.Instructions.Clear();
            mc.Body.Variables.Clear();
            mc.Body.ExceptionHandlers.Clear();
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);

            var writerParams = modder.Methods.Single(m => m.Name == "get_WriterParameters");
            il = writerParams.Body.GetILProcessor();
            var get_Current = writerParams.Body.Instructions.Single(x => x.Operand is MethodReference mref && mref.Name == "get_Current");
            il.Remove(get_Current.Next);
            il.Remove(get_Current.Next);
            il.Replace(get_Current, Instruction.Create(
                OpCodes.Ldc_I4, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 37 : 0
            ));

            asm.Write(dllPath);
        }
    }

    public static int Main(string[] args)
    {
        // MonoMod v22 (as pulled by ModFramework) has outdated GAC/writer behaviour on NET9;
        // patch the copy we ship so the ported MonoMod.Cil/MonoMod.Utils code behaves predictably.
        try { PatchMonoMod(); } catch (Exception ex) { Console.WriteLine($"[warn] PatchMonoMod skipped: {ex.Message}"); }

        // Deployed next to TShock.Server.exe: with no explicit inputs, migrate every plugin
        // in the server's ServerPlugins folder (in place, a .bak is kept per file).
        if (args.Length == 0)
        {
            string sp = Path.Combine(Environment.CurrentDirectory, "ServerPlugins");
            if (Directory.Exists(sp))
            {
                var dlls = Directory.GetFiles(sp, "*.dll");
                if (dlls.Length > 0)
                {
                    Console.WriteLine($"migrating {dlls.Length} plugin(s) from {sp} ...");
                    args = dlls;
                }
            }
        }

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                RefTile.PluginMigrator - migrates old OTAPI/TShock plugin assemblies to the ref-tile model.

                Usage:
                  RefTile.PluginMigrator                      # migrate every *.dll in ./ServerPlugins (in place, .bak kept)
                  RefTile.PluginMigrator <plugin.dll> [<plugin2.dll> ...] [-o <outputDir>]
                  RefTile.PluginMigrator --selftest

                Rewrites Terraria.ITile / Terraria.Tile references to Terraria.TileData
                (types, fields, property accessors, method calls, newobj, null handling and
                Main.tile access). Without -o the input file is overwritten (a .bak is kept).
                """);
            return 0;
        }

        if (args.Contains("--selftest"))
            return SelfTest.Run() ? 0 : 1;

        // --universal uses the ported TileSystemPatchLogic (the same conversion machinery that
        // rewrites the whole vanilla game) instead of the pattern-based RewriteBody.
        if (args.Contains("--universal"))
            return UniversalMain(args);

        string? outputDir = null;
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length) { outputDir = args[i + 1]; i++; }
            else if (args[i].StartsWith("-o=", StringComparison.Ordinal)) { outputDir = args[i][3..]; }
            else if (!args[i].StartsWith("-")) inputs.Add(args[i]);
        }

        int failed = 0;
        foreach (var input in inputs)
        {
            try
            {
                string output = Migrator.Migrate(input, outputDir);
                Console.WriteLine($"[OK] {Path.GetFileName(input)} -> {output}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"[FAIL] {input}: {ex.Message}");
            }
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Runs the ported TileSystemPatchLogic (universal converter) on the input plugin(s).</summary>
    static int UniversalMain(string[] args)
    {
        string? outputDir = null;
        string? refPath = null;
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length) { outputDir = args[i + 1]; i++; }
            else if (args[i].StartsWith("-o=", StringComparison.Ordinal)) { outputDir = args[i][3..]; }
            else if (args[i] == "-ref" && i + 1 < args.Length) { refPath = args[i + 1]; i++; }
            else if (!args[i].StartsWith("-")) inputs.Add(args[i]);
        }

        int failed = 0;
        foreach (var input in inputs)
        {
            try
            {
                string output = UniversalMigrate(input, outputDir, refPath);
                Console.WriteLine($"[OK] {Path.GetFileName(input)} -> {output}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"[FAIL] {input}: {ex}");
            }
        }
        return failed == 0 ? 0 : 1;
    }

    static string UniversalMigrate(string input, string? outputDir, string? refPath)
    {
        if (!File.Exists(input)) throw new FileNotFoundException(input);

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);
        resolver.AddSearchDirectory(AppContext.BaseDirectory);
        if (refPath is not null)
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(refPath))!);
        // The tool is deployed next to TShock.Server.exe, so the ref-tile TerrariaServer / TShock
        // assemblies and the ServerPlugins folder are all discoverable from AppContext.BaseDirectory
        // (dev builds keep the runtime DLLs in a bin/ subfolder next to the executable).
        string baseDir = AppContext.BaseDirectory;
        string[] extraDirs =
        {
            baseDir,
            Path.Combine(baseDir, "bin"),
            Path.Combine(baseDir, "ServerPlugins"),
        };
        foreach (var d in extraDirs)
            if (Directory.Exists(d)) resolver.AddSearchDirectory(d);

        bool hasPdb = File.Exists(Path.ChangeExtension(input, ".pdb"));
        using var module = ModuleDefinition.ReadModule(input,
            new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = hasPdb });

        new TileSystemPatchLogic(new ModuleContext(module)).Patch();
        // complement the game-oriented converter with the plugin-pattern pass
        Migrator.ApplyToModule(module, out _);
        ImportOutOfModuleRefs(module);

        string output;
        if (outputDir != null)
        {
            Directory.CreateDirectory(outputDir);
            output = Path.Combine(outputDir, Path.GetFileName(input));
        }
        else
        {
            output = input;
            File.Copy(input, input + ".bak", overwrite: true);
        }
        module.Write(output, new WriterParameters { WriteSymbols = hasPdb });
        return output;
    }

    /// <summary>
    /// The ported patch resolves Terraria types from the referenced OTAPI assembly and uses those
    /// definitions directly as operands/signatures. Cecil refuses to write references that belong to
    /// another module, so every out-of-module reference is imported into the plugin module first.
    /// </summary>
    static void ImportOutOfModuleRefs(ModuleDefinition module)
    {
        foreach (var type in module.GetAllTypes())
        {
            foreach (var f in type.Fields)
            {
                if (f.FieldType is not GenericParameter && f.FieldType.Module != module)
                    f.FieldType = module.ImportReference(f.FieldType);
            }
            foreach (var m in type.Methods)
            {
                foreach (var p in m.Parameters)
                    if (p.ParameterType is not GenericParameter && p.ParameterType.Module != module)
                        p.ParameterType = module.ImportReference(p.ParameterType);
                if (m.ReturnType is not GenericParameter && m.ReturnType.Module != module)
                    m.ReturnType = module.ImportReference(m.ReturnType);
                if (m.HasBody)
                {
                    foreach (var v in m.Body.Variables)
                        if (v.VariableType is not GenericParameter && v.VariableType.Module != module)
                            v.VariableType = module.ImportReference(v.VariableType);
                    foreach (var inst in m.Body.Instructions)
                    {
                        switch (inst.Operand)
                        {
                            case MethodReference mr when mr.Module != module:
                                inst.Operand = module.ImportReference(mr);
                                break;
                            case FieldReference fr when fr.Module != module:
                                inst.Operand = module.ImportReference(fr);
                                break;
                            case TypeReference tr when tr is not GenericParameter && tr.Module != module:
                                inst.Operand = module.ImportReference(tr);
                                break;
                        }
                    }
                }
            }
        }
    }
}

/// <summary>Rewrites old tile references in a plugin assembly. Works purely by type/member name.</summary>
public static class Migrator
{
    public const string OldTileIf = "Terraria.ITile";
    public const string OldTileClass = "Terraria.Tile";
    public const string NewTile = "Terraria.TileData";
    public const string NewCollection = "Terraria.TileCollection";
    public const string MainType = "Terraria.Main";

    public static string Migrate(string input, string? outputDir)
    {
        if (!File.Exists(input)) throw new FileNotFoundException(input);

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);
        resolver.AddSearchDirectory(AppContext.BaseDirectory);
        bool hasPdb = File.Exists(Path.ChangeExtension(input, ".pdb"));
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = hasPdb };

        using var module = ModuleDefinition.ReadModule(input, rp);
        ApplyToModule(module, out var stats);

        string output;
        if (outputDir != null)
        {
            Directory.CreateDirectory(outputDir);
            output = Path.Combine(outputDir, Path.GetFileName(input));
        }
        else
        {
            output = input;
            File.Copy(input, input + ".bak", overwrite: true);
        }

        module.Write(output, new WriterParameters { WriteSymbols = hasPdb });
        Console.WriteLine($"  tile-refs rewritten: types={stats.tiles} fields={stats.fields} accessors={stats.accessors} methods={stats.methods} newobj={stats.news} nulls={stats.nulls} collection={stats.collections} ldobj={stats.ldobj}");
        return output;
    }

    /// <summary>
    /// Pattern-based rewrite pass over an already-loaded module. Runs after the ported
    /// TileSystemPatchLogic in --universal mode to catch plugin-specific leftovers that the
    /// game-oriented converter does not recognise (ICollection&lt;ITile&gt; access, plugin-owned
    /// Tile[,] arrays, ITile-parameter helpers, castclass/isinst, Main.tile field access...).
    /// </summary>
    public static void ApplyToModule(ModuleDefinition module, out Stats stats)
    {
        stats = new Stats();

        foreach (var type in module.GetAllTypes())
        {
            foreach (var f in type.Fields)
                if (TryMap(f.FieldType, out var nt)) { f.FieldType = nt; stats.fields++; }
            foreach (var p in type.Properties)
                if (TryMap(p.PropertyType, out var nt)) p.PropertyType = nt;

            foreach (var m in type.Methods)
            {
                if (TryMap(m.ReturnType, out var nret)) m.ReturnType = nret;
                foreach (var p in m.Parameters)
                    if (TryMap(p.ParameterType, out var npt)) p.ParameterType = npt;
                if (m.HasGenericParameters)
                    foreach (var gp in m.GenericParameters)
                        foreach (var c in gp.Constraints)
                            if (TryMap(c.ConstraintType, out var nct)) c.ConstraintType = nct;
                if (!m.HasBody) continue;

                foreach (var v in m.Body.Variables)
                    if (TryMap(v.VariableType, out var nvt)) v.VariableType = nvt;

                RewriteBody(module, m, stats);

                // TileData field access (ldfld/stfld) needs the instance as an address; a plain
                // value load (ldloc/ldarg of a TileData) before it makes the JIT misread the
                // 16-byte value as a pointer -> 0xC0000005 while compiling.
                foreach (var inst in m.Body.Instructions)
                {
                    if (inst.OpCode == OpCodes.Ldfld && inst.Operand is FieldReference ff1 && ff1.DeclaringType.FullName == NewTile)
                        FixThisAddress(m, inst, 1);
                    else if (inst.OpCode == OpCodes.Stfld && inst.Operand is FieldReference ff2 && ff2.DeclaringType.FullName == NewTile)
                        FixThisAddress(m, inst, 2);
                }
            }
        }

        foreach (var attr in module.CustomAttributes)
            _ = attr; // AttributeType is read-only; attribute rewriting is skipped (rarely needed)
    }

    // ---------------- type mapping ----------------

    public static bool IsOldTile(string fullName) => fullName == OldTileIf || fullName == OldTileClass;

    static TypeReference MakeTypeRef(ModuleDefinition module, string fullName)
    {
        int dot = fullName.LastIndexOf('.');
        // Critical: the type must resolve against the SAME assembly the plugin references for
        // Terraria types (OTAPI), NOT System.Runtime/CoreLibrary, or the runtime will throw
        // TypeLoadException looking for Terraria.TileData in the wrong assembly.
        // TileData is a struct: the valuetype flag must match or the runtime throws
        // "TypeLoadException ... due to value type mismatch".
        return new TypeReference(fullName[..dot], fullName[(dot + 1)..], module, FindTerrariaScope(module))
        {
            IsValueType = fullName == NewTile,
        };
    }

    /// <summary>
    /// Finds the assembly reference the plugin uses for Terraria types (OTAPI / TerrariaServer / ...),
    /// so synthesized Terraria.TileData / Terraria.TileCollection references resolve correctly.
    /// </summary>
    static IMetadataScope? FindTerrariaScope(ModuleDefinition module)
    {
        foreach (var ar in module.AssemblyReferences)
            if (ar.Name is "OTAPI" or "TerrariaServer" or "Terraria" or "TerrariaServerAPI" or "tModLoader")
                return ar;
        foreach (var tr in module.GetTypeReferences())
            if (tr.FullName.StartsWith("Terraria.", StringComparison.Ordinal))
                return tr.Scope;
        return module.TypeSystem.CoreLibrary;
    }

    public static TypeReference TileData(ModuleDefinition m) => MakeTypeRef(m, NewTile);
    static TypeReference TileCollection(ModuleDefinition m) => MakeTypeRef(m, NewCollection);

    /// <summary>Deep-maps ITile/Tile to TileData (arrays, byrefs, generics).</summary>
    static bool TryMap(TypeReference t, out TypeReference mapped)
    {
        mapped = t;
        switch (t)
        {
            case ArrayType arr:
                if (TryMap(arr.ElementType, out var ne)) { mapped = new ArrayType(ne, arr.Rank); return true; }
                return false;
            case ByReferenceType br:
                if (TryMap(br.ElementType, out var nb)) { mapped = new ByReferenceType(nb); return true; }
                return false;
            case PointerType pt:
                if (TryMap(pt.ElementType, out var np)) { mapped = new PointerType(np); return true; }
                return false;
            case GenericInstanceType git:
            {
                var args = new List<TypeReference>();
                bool any = false;
                foreach (var arg in git.GenericArguments)
                    if (TryMap(arg, out var na)) { args.Add(na); any = true; }
                    else args.Add(arg);
                if (any) { var g = new GenericInstanceType(git.ElementType); foreach (var a in args) g.GenericArguments.Add(a); mapped = g; return true; }
                return false;
            }
            default:
                if (IsOldTile(t.FullName))
                {
                    // preserve the original reference's scope (the OTAPI assembly), only change the name;
                    // TileData is a struct so the valuetype flag must be set
                    mapped = new TypeReference("Terraria", "TileData", t.Module, t.Scope) { IsValueType = true };
                    return true;
                }
                return false;
        }
    }

    /// <summary>True when a method reference involves old tile types in its declaring type, parameters or return.</summary>
    static bool CalleeReferencesOldTile(MethodReference callee)
    {
        if (IsOldTile(callee.DeclaringType.FullName)) return true;
        if (ReferencesOldTile(callee.ReturnType)) return true;
        if (callee.Parameters.Any(p => ReferencesOldTile(p.ParameterType))) return true;
        return false;
    }

    static bool ReferencesOldTile(TypeReference t)
    {
        switch (t)
        {
            case ArrayType a: return ReferencesOldTile(a.ElementType);
            case ByReferenceType b: return ReferencesOldTile(b.ElementType);
            case PointerType p: return ReferencesOldTile(p.ElementType);
            case GenericInstanceType g: return g.GenericArguments.Any(ReferencesOldTile);
            default: return IsOldTile(t.FullName);
        }
    }

    /// <summary>Clones a method reference onto TileData, mapping ITile/Tile parameter and return types.</summary>
    static MethodReference RemapCallee(ModuleDefinition module, MethodReference callee, string? rename = null, TypeReference? returnOverride = null)
    {
        // declaring type: ITile/Tile -> TileData; List<ITile> -> List<TileData>; others unchanged
        var declaring = TryMap(callee.DeclaringType, out var nd) ? nd : callee.DeclaringType;
        var mr = new MethodReference(rename ?? callee.Name, returnOverride ?? callee.ReturnType, declaring)
        {
            HasThis = callee.HasThis,
            ExplicitThis = callee.ExplicitThis,
            CallingConvention = callee.CallingConvention,
        };
        foreach (var gp in callee.GenericParameters) mr.GenericParameters.Add(new GenericParameter(gp.Name, mr));
        foreach (var p in callee.Parameters)
        {
            var np = new ParameterDefinition(p.Name, p.Attributes, p.ParameterType);
            mr.Parameters.Add(np);
        }
        // deep-map parameter + return types on the clone
        for (int i = 0; i < mr.Parameters.Count; i++)
            if (TryMap(mr.Parameters[i].ParameterType, out var npt)) mr.Parameters[i].ParameterType = npt;
        if (TryMap(mr.ReturnType, out var nrt)) mr.ReturnType = nrt;
        return mr;
    }

    // ---------------- body rewriting ----------------

    static void RewriteBody(ModuleDefinition module, MethodDefinition m, Stats stats)
    {
        var snapshot = m.Body.Instructions.ToArray();
        var il = m.Body.GetILProcessor();

        foreach (var inst in snapshot)
        {
            switch (inst.Operand)
            {
                case TypeReference tr when TryMap(tr, out var nt):
                    // castclass/isinst are only valid for reference types; a struct tile must be unboxed.
                    // If the value feeding the unbox comes from a raw TileData value (e.g. TileData.New()
                    // in a "cond ? (TileData)x.Clone() : new Tile()" pattern), it must be boxed first,
                    // otherwise unbox.any misreads the struct as an object reference and corrupts memory.
                    if (inst.OpCode == OpCodes.Castclass || inst.OpCode == OpCodes.Isinst)
                    {
                        inst.OpCode = OpCodes.Unbox_Any;
                        inst.Operand = nt;
                        var boxProducer = FindThisProducerBackward(m, inst, 1);
                        if (boxProducer is not null && PushesTileDataValue(m, boxProducer))
                        {
                            il.InsertAfter(boxProducer, Instruction.Create(OpCodes.Box, TileData(module)));
                        }
                        stats.tiles++;
                    }
                    else
                    {
                        inst.Operand = nt;
                        stats.tiles++;
                    }
                    break;

                case FieldReference fref:
                    if (fref.DeclaringType.FullName == MainType && fref.Name == "tile")
                    {
                        inst.Operand = new FieldReference("tile", TileCollection(module), fref.DeclaringType);
                        stats.collections++;
                    }
                    else if (IsOldTile(fref.DeclaringType.FullName))
                    {
                        inst.Operand = new FieldReference(fref.Name, TileData(module), TileData(module));
                        stats.fields++;
                    }
                    break;

                case MethodReference callee:
                    RewriteCall(module, m, il, inst, callee, stats);
                    break;
            }

            RewriteNulls(module, m, il, inst, stats);
        }
    }

    static void RewriteCall(ModuleDefinition module, MethodDefinition method, ILProcessor il, Instruction inst, MethodReference callee, Stats stats)
    {
        string decl = callee.DeclaringType.FullName;

        // Main.tile[x,y] via old array (Tile[,]/ITile[,]) or ModFramework.DefaultCollection/ICollection accessors.
        // IMPORTANT: only accesses backed by the Main.tile field become TileCollection::get_Item;
        // the plugin's own Tile[,] fields/locals became TileData[,] struct arrays and keep using Get/Set.
        bool isOldArray = decl.StartsWith("Terraria.Tile[", StringComparison.Ordinal)
            || decl.StartsWith("Terraria.ITile[", StringComparison.Ordinal);
        bool isCollection = decl.StartsWith("ModFramework.DefaultCollection`1", StringComparison.Ordinal)
            || decl.StartsWith("ModFramework.ICollection`1", StringComparison.Ordinal);
        if (isOldArray || isCollection)
        {
            bool isMainTileAccess = isCollection
                || (isOldArray && FindThisProducerBackward(method, inst, 3) is Instruction arrProd
                    && arrProd.OpCode == OpCodes.Ldsfld
                    && arrProd.Operand is FieldReference afr
                    && afr.DeclaringType.FullName == "Terraria.Main" && afr.Name == "tile");

            var getItem = GetItemRef(module);
            if (callee.Name == "Get" || callee.Name == "get_Item")
            {
                if (isMainTileAccess)
                {
                    inst.OpCode = OpCodes.Callvirt;
                    inst.Operand = getItem;
                    stats.collections++;
                }
                else
                {
                    // plugin-owned Tile[,] field/local: became TileData[,]; Get returns ref TileData
                    inst.OpCode = OpCodes.Call;
                    inst.Operand = TileArrayGet(module);
                    stats.collections++;
                }
                FixGetItemConsumer(module, il, inst, stats);
                return;
            }
            if (callee.Name == "Set" || callee.Name == "set_Item")
            {
                if (isMainTileAccess)
                {
                    // Main.tile has no set_Item: this[x,y] = value -> ref t = get_Item(x,y); t = value
                    var local = new VariableDefinition(TileData(module));
                    method.Body.Variables.Add(local);
                    var store = BuildStloc(method, local);
                    inst.OpCode = store.OpCode;
                    inst.Operand = store.Operand;
                    var prev = inst;
                    foreach (var i in new[]
                    {
                        Instruction.Create(OpCodes.Callvirt, getItem),
                        Instruction.Create(OpCodes.Ldloc, local),
                        Instruction.Create(OpCodes.Stobj, TileData(module)),
                    })
                    {
                        il.InsertAfter(prev, i);
                        prev = i;
                    }
                }
                else
                {
                    // plugin-owned Tile[,]: became TileData[,]; Set(x, y, TileData) still works
                    inst.OpCode = OpCodes.Call;
                    inst.Operand = TileArraySet(module);
                }
                stats.collections++;
                return;
            }
        }

        // new Tile[w,h] -> new TileData[w,h]
        // (the plugin's own Tile[,]/ITile[,] fields became TileData[,] struct arrays, but the
        // array constructor was left behind; allocating the old reference-array would make every
        // later Get/Set read out of bounds -> 0xC0000005 at JIT time)
        if (inst.OpCode == OpCodes.Newobj && callee.Name == ".ctor"
            && callee.DeclaringType is ArrayType oldArr && IsOldTile(oldArr.ElementType.FullName))
        {
            var newArr = new ArrayType(TileData(module), oldArr.Rank);
            var ctor = new MethodReference(".ctor", module.TypeSystem.Void, newArr) { HasThis = true };
            foreach (var p in callee.Parameters)
                ctor.Parameters.Add(new ParameterDefinition(p.ParameterType));
            inst.Operand = ctor;
            stats.news++;
            return;
        }

        // TileData instance methods (Clone, active, get_IsNull/IsNotNull, ...) need the 'this'
        // as an address; the plugin's locals/args holding TileData values must be loaded with
        // ldloca/ldarga for the call or the JIT misreads the 16-byte value as a pointer (AV).
        if (callee.DeclaringType.FullName == NewTile)
        {
            if (callee.HasThis)
            {
                FixThisAddress(method, inst, callee.Parameters.Count + 1);
                stats.methods++;
            }
            return;
        }

        if (!CalleeReferencesOldTile(callee)) return;

        // new Tile() / new Tile(Tile) -> TileData.New()
        if (inst.OpCode == OpCodes.Newobj && callee.Name == ".ctor")
        {
            var newRef = new MethodReference("New", TileData(module), TileData(module)) { HasThis = false };
            foreach (var p in callee.Parameters)
            {
                var np = new ParameterDefinition(p.ParameterType);
                newRef.Parameters.Add(np);
            }
            for (int i = 0; i < newRef.Parameters.Count; i++)
                if (TryMap(newRef.Parameters[i].ParameterType, out var npt)) newRef.Parameters[i].ParameterType = npt;
            inst.OpCode = OpCodes.Call;
            inst.Operand = newRef;
            stats.news++;
            return;
        }

        // property accessor -> direct field
        if (callee.Name.StartsWith("get_") || callee.Name.StartsWith("set_"))
        {
            string fieldName = callee.Name[4..];
            bool isGet = callee.Name.StartsWith("get_");
            inst.OpCode = isGet ? OpCodes.Ldfld : OpCodes.Stfld;
            inst.Operand = new FieldReference(fieldName, TileData(module), TileData(module));
            FixThisAddress(method, inst, isGet ? 1 : 2); // ldfld consumes 1, stfld consumes [address, value]
            stats.accessors++;
            return;
        }

        // other instance/static method -> TileData method (signature-preserving clone)
        var mapped = RemapCallee(module, callee);
        inst.OpCode = OpCodes.Call;
        inst.Operand = mapped;
        if (callee.HasThis)
        {
            // the call consumes 'this' plus all parameters
            FixThisAddress(method, inst, callee.Parameters.Count + 1);
        }
        stats.methods++;
    }

    /// <summary>
    /// Converts a value-producing 'this' load into an address-producing one for struct calls.
    /// Uses a local backward stack walk (exact within a straight-line block, works inside branches).
    /// </summary>
    static void FixThisAddress(MethodDefinition method, Instruction consumer, int consumedItems)
    {
        var producer = FindThisProducerBackward(method, consumer, consumedItems);
        if (producer is null) return;

        switch (producer.OpCode.Code)
        {
            case Code.Ldloc_0: producer.OpCode = OpCodes.Ldloca_S; producer.Operand = method.Body.Variables[0]; break;
            case Code.Ldloc_1: producer.OpCode = OpCodes.Ldloca_S; producer.Operand = method.Body.Variables[1]; break;
            case Code.Ldloc_2: producer.OpCode = OpCodes.Ldloca_S; producer.Operand = method.Body.Variables[2]; break;
            case Code.Ldloc_3: producer.OpCode = OpCodes.Ldloca_S; producer.Operand = method.Body.Variables[3]; break;
            case Code.Ldloc_S: producer.OpCode = OpCodes.Ldloca_S; break;
            case Code.Ldloc: producer.OpCode = OpCodes.Ldloca; break;
            case Code.Ldarg_0:
                if (method.HasThis) { producer.OpCode = OpCodes.Ldarga_S; producer.Operand = method.Body.ThisParameter; }
                else if (method.Parameters.Count > 0) { producer.OpCode = OpCodes.Ldarga_S; producer.Operand = method.Parameters[0]; }
                break;
            case Code.Ldarg_1:
            case Code.Ldarg_2:
            case Code.Ldarg_3:
            {
                int idx = producer.OpCode.Code - Code.Ldarg_0 - (method.HasThis ? 1 : 0);
                if (idx >= 0 && idx < method.Parameters.Count) { producer.OpCode = OpCodes.Ldarga_S; producer.Operand = method.Parameters[idx]; }
                break;
            }
            case Code.Ldarg_S: producer.OpCode = OpCodes.Ldarga_S; break;
            case Code.Ldarg: producer.OpCode = OpCodes.Ldarga; break;
            case Code.Ldfld: producer.OpCode = OpCodes.Ldflda; break;
            case Code.Call:
            case Code.Callvirt:
                // a call that already returns ref TileData is an address - leave it
                if (producer.Operand is MethodReference mrRet && mrRet.ReturnType is ByReferenceType brRet && brRet.ElementType.FullName == NewTile)
                    break;
                goto case Code.Newobj;
            case Code.Newobj:
            {
                // a value-producing tile producer: spill to a temp so we can take its address
                var t = TileData(method.Module);
                var tmp = new VariableDefinition(t);
                method.Body.Variables.Add(tmp);
                var st = BuildStloc(method, tmp);
                producer.OpCode = st.OpCode;
                producer.Operand = st.Operand;
                method.Body.GetILProcessor().InsertAfter(producer,
                    Instruction.Create(OpCodes.Ldloca_S, tmp));
                break;
            }
        }
    }

    /// <summary>
    /// Local backward stack walk: finds the instruction that pushed the 'this' for the consumer,
    /// accounting for intermediate instructions that push/pop. Stops at control flow.
    /// </summary>
    static Instruction? FindThisProducerBackward(MethodDefinition method, Instruction consumer, int consumedItems)
    {
        int need = consumedItems;
        for (Instruction? cur = consumer.Previous; cur is not null; cur = cur.Previous)
        {
            switch (cur.OpCode.FlowControl)
            {
                case FlowControl.Branch:
                case FlowControl.Cond_Branch:
                case FlowControl.Return:
                case FlowControl.Throw:
                    return null;
            }
            int push = GetPushCount(method, cur);
            int pop = GetPopCount(method, cur);
            if (need <= push) return cur;
            need = need - push + pop;
        }
        return null;
    }

    static int GetPopCount(MethodDefinition method, Instruction inst)
    {
        if (inst.OpCode == OpCodes.Ret) return method.ReturnType.FullName == "System.Void" ? 0 : 1;
        switch (inst.OpCode.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1:
            case StackBehaviour.Popi:
            case StackBehaviour.Popref: return 1;
            case StackBehaviour.Pop1_pop1:
            case StackBehaviour.Popi_pop1:
            case StackBehaviour.Popi_popi:
            case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4:
            case StackBehaviour.Popi_popr8:
            case StackBehaviour.Popref_pop1:
            case StackBehaviour.Popref_popi: return 2;
            case StackBehaviour.Popi_popi_popi:
            case StackBehaviour.Popref_popi_popr4:
            case StackBehaviour.Popref_popi_popr8:
            case StackBehaviour.Popref_popi_popi:
            case StackBehaviour.Popref_popi_popi8:
            case StackBehaviour.Popref_popi_popref: return 3;
            case StackBehaviour.PopAll: return 0;
            case StackBehaviour.Varpop:
                if (inst.OpCode == OpCodes.Call || inst.OpCode == OpCodes.Callvirt || inst.OpCode == OpCodes.Newobj)
                {
                    var mref = (MethodReference)inst.Operand!;
                    int c = mref.Parameters.Count + (inst.OpCode != OpCodes.Newobj && mref.HasThis ? 1 : 0);
                    return c;
                }
                if (inst.OpCode == OpCodes.Calli) return ((CallSite)inst.Operand!).Parameters.Count + 1;
                return 0;
            default: return 0;
        }
    }

    static int GetPushCount(MethodDefinition method, Instruction inst)
    {
        switch (inst.OpCode.StackBehaviourPush)
        {
            case StackBehaviour.Push0: return 0;
            case StackBehaviour.Push1:
            case StackBehaviour.Pushi:
            case StackBehaviour.Pushi8:
            case StackBehaviour.Pushr4:
            case StackBehaviour.Pushr8:
            case StackBehaviour.Pushref: return 1;
            case StackBehaviour.Push1_push1: return 2;
            case StackBehaviour.Varpush:
                if (inst.OpCode == OpCodes.Call || inst.OpCode == OpCodes.Callvirt)
                    return ((MethodReference)inst.Operand!).ReturnType.FullName == "System.Void" ? 0 : 1;
                if (inst.OpCode == OpCodes.Newobj) return 1;
                if (inst.OpCode == OpCodes.Calli) return ((CallSite)inst.Operand!).ReturnType.FullName == "System.Void" ? 0 : 1;
                return 0;
            default: return 0;
        }
    }

    // ---------------- null handling ----------------

    static void RewriteNulls(ModuleDefinition module, MethodDefinition method, ILProcessor il, Instruction inst, Stats stats)
    {
        // null tile local init: "ldnull; stloc TileData" -> "ldsfld TileData::NULL; stloc"
        if (inst.OpCode == OpCodes.Ldnull)
        {
            var next = inst.Next;
            if (next is not null && StoresToTileLocal(method, next))
            {
                inst.OpCode = OpCodes.Ldsfld;
                inst.Operand = new FieldReference("NULL", TileData(module), TileData(module));
                stats.nulls++;
            }
            return;
        }

        // tile == null / null == tile
        if (inst.OpCode == OpCodes.Ceq)
        {
            var v1 = inst.Previous;
            var v2 = v1?.Previous;
            if (v1 is null || v2 is null) return;
            bool v1Null = v1.OpCode == OpCodes.Ldnull;
            bool v2Null = v2.OpCode == OpCodes.Ldnull;
            if ((v1Null && !v2Null) || (v2Null && !v1Null))
            {
                var tileSide = v1Null ? v2 : v1;
                if (!LooksLikeTileLoad(method, tileSide)) return;
                var nullSide = v1Null ? v1 : v2;
                nullSide.OpCode = OpCodes.Ldsfld;
                nullSide.Operand = new FieldReference("NULL", TileData(module), TileData(module));
                inst.OpCode = OpCodes.Call;
                inst.Operand = new MethodReference("op_Equality", module.TypeSystem.Boolean, TileData(module))
                {
                    HasThis = false,
                    Parameters = { new ParameterDefinition(TileData(module)), new ParameterDefinition(TileData(module)) },
                };
                stats.nulls++;
            }
            return;
        }

        // if (tile) / if (!tile)
        if (inst.OpCode == OpCodes.Brtrue || inst.OpCode == OpCodes.Brtrue_S
            || inst.OpCode == OpCodes.Brfalse || inst.OpCode == OpCodes.Brfalse_S)
        {
            var valueLoad = inst.Previous;
            if (valueLoad is null || !LooksLikeTileLoad(method, valueLoad)) return;
            bool isTrue = inst.OpCode == OpCodes.Brtrue || inst.OpCode == OpCodes.Brtrue_S;
            var getter = new MethodReference(isTrue ? "get_IsNotNull" : "get_IsNull", module.TypeSystem.Boolean, TileData(module))
            { HasThis = true };
            FixThisAddress(method, inst, 1); // the tile value is the single input of the getter call
            il.InsertBefore(inst, Instruction.Create(OpCodes.Call, getter));
            stats.nulls++;
        }
    }

    /// <summary>True when the instruction is a stloc into a TileData local.</summary>
    static bool StoresToTileLocal(MethodDefinition method, Instruction inst)
    {
        VariableDefinition? v = null;
        switch (inst.OpCode.Code)
        {
            case Code.Stloc_0: if (method.Body.Variables.Count > 0) v = method.Body.Variables[0]; break;
            case Code.Stloc_1: if (method.Body.Variables.Count > 1) v = method.Body.Variables[1]; break;
            case Code.Stloc_2: if (method.Body.Variables.Count > 2) v = method.Body.Variables[2]; break;
            case Code.Stloc_3: if (method.Body.Variables.Count > 3) v = method.Body.Variables[3]; break;
            case Code.Stloc_S:
            case Code.Stloc:
                v = (VariableDefinition)inst.Operand!;
                break;
        }
        return v is not null && v.VariableType.FullName == NewTile;
    }

    /// <summary>True when the instruction consumes the top stack item as a value rather than an address.</summary>
    static bool IsValueConsumer(Instruction inst)
    {
        switch (inst.OpCode.Code)
        {
            case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
            case Code.Stloc_S: case Code.Stloc:
            case Code.Starg_S: case Code.Starg:
            case Code.Ret:
                return true;
            case Code.Call:
            case Code.Callvirt:
                // a tile-instance call consumes the item as 'this' (an address)
                if (inst.Operand is MethodReference mr && IsOldTile(mr.DeclaringType.FullName)) return false;
                return true; // otherwise it is most likely an argument
            default:
                return false;
        }
    }

    /// <summary>True when the instruction pushes a raw (unboxed) TileData value.</summary>
    static bool PushesTileDataValue(MethodDefinition method, Instruction producer)
    {
        TypeReference? t = null;
        switch (producer.OpCode.Code)
        {
            case Code.Call:
            case Code.Callvirt:
            case Code.Newobj:
                t = ((MethodReference)producer.Operand!).ReturnType;
                break;
            case Code.Ldsfld:
            case Code.Ldfld:
                t = ((FieldReference)producer.Operand!).FieldType;
                break;
            case Code.Ldobj:
                t = (TypeReference)producer.Operand!;
                break;
            case Code.Ldloc_0: if (method.Body.Variables.Count > 0) t = method.Body.Variables[0].VariableType; break;
            case Code.Ldloc_1: if (method.Body.Variables.Count > 1) t = method.Body.Variables[1].VariableType; break;
            case Code.Ldloc_2: if (method.Body.Variables.Count > 2) t = method.Body.Variables[2].VariableType; break;
            case Code.Ldloc_3: if (method.Body.Variables.Count > 3) t = method.Body.Variables[3].VariableType; break;
            case Code.Ldloc_S:
            case Code.Ldloc:
                t = ((VariableDefinition)producer.Operand!).VariableType;
                break;
        }
        return t is not null && t is not ByReferenceType && t.FullName == NewTile;
    }

    /// <summary>True when the instruction loads a value whose type is TileData (after remap).</summary>
    static bool LooksLikeTileLoad(MethodDefinition method, Instruction inst)
    {
        TypeReference? t = null;
        switch (inst.OpCode.Code)
        {
            case Code.Ldloc_0: if (method.Body.Variables.Count > 0) t = method.Body.Variables[0].VariableType; break;
            case Code.Ldloc_1: if (method.Body.Variables.Count > 1) t = method.Body.Variables[1].VariableType; break;
            case Code.Ldloc_2: if (method.Body.Variables.Count > 2) t = method.Body.Variables[2].VariableType; break;
            case Code.Ldloc_3: if (method.Body.Variables.Count > 3) t = method.Body.Variables[3].VariableType; break;
            case Code.Ldloc_S:
            case Code.Ldloc:
            case Code.Ldloca_S:
            case Code.Ldloca:
                t = ((VariableDefinition)inst.Operand!).VariableType;
                break;
            case Code.Ldarg_0:
                t = method.HasThis ? method.Body.ThisParameter.ParameterType
                    : (method.Parameters.Count > 0 ? method.Parameters[0].ParameterType : null);
                break;
            case Code.Ldarg_1: if (method.Parameters.Count > (method.HasThis ? 0 : 1)) t = method.Parameters[method.HasThis ? 0 : 1].ParameterType; break;
            case Code.Ldarg_2: if (method.Parameters.Count > (method.HasThis ? 1 : 2)) t = method.Parameters[method.HasThis ? 1 : 2].ParameterType; break;
            case Code.Ldarg_3: if (method.Parameters.Count > (method.HasThis ? 2 : 3)) t = method.Parameters[method.HasThis ? 2 : 3].ParameterType; break;
            case Code.Ldarg_S:
            case Code.Ldarg:
                t = ((ParameterDefinition)inst.Operand!).ParameterType;
                break;
            case Code.Ldfld:
            case Code.Ldflda:
                t = ((FieldReference)inst.Operand!).FieldType;
                break;
            case Code.Call:
            case Code.Callvirt:
                t = ((MethodReference)inst.Operand!).ReturnType;
                break;
            default:
                return false;
        }
        return t is not null && (t.FullName == NewTile || (t is ByReferenceType br && br.ElementType.FullName == NewTile));
    }

    // ---------------- helpers ----------------

    static MethodReference GetItemRef(ModuleDefinition module)
    {
        var t = TileCollection(module);
        var mr = new MethodReference("get_Item", new ByReferenceType(TileData(module)), t) { HasThis = true };
        mr.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        mr.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        return mr;
    }

    /// <summary>TileData[,]::Get(x, y) returning ref TileData.</summary>
    static MethodReference TileArrayGet(ModuleDefinition module)
    {
        var arr = new ArrayType(TileData(module), 2);
        var mr = new MethodReference("Get", new ByReferenceType(TileData(module)), arr) { HasThis = true };
        mr.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        mr.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        return mr;
    }

    /// <summary>TileData[,]::Set(x, y, TileData).</summary>
    static MethodReference TileArraySet(ModuleDefinition module)
    {
        var arr = new ArrayType(TileData(module), 2);
        var mr = new MethodReference("Set", module.TypeSystem.Void, arr) { HasThis = true };
        mr.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        mr.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        mr.Parameters.Add(new ParameterDefinition(TileData(module)));
        return mr;
    }

    /// <summary>
    /// After a tile access now returns ref TileData, adjust the consumer: a following ldind.ref
    /// is removed when it feeds a tile method call (address use) or becomes ldobj (value use);
    /// a direct value consumer (stloc/arg/ret) gets an ldobj inserted before it.
    /// </summary>
    static void FixGetItemConsumer(ModuleDefinition module, ILProcessor il, Instruction inst, Stats stats)
    {
        var next = inst.Next;
        if (next is null) return;
        if (next.OpCode == OpCodes.Ldind_Ref)
        {
            var after = next.Next;
            bool usedAsThis = after is not null
                && (after.OpCode == OpCodes.Callvirt || after.OpCode == OpCodes.Call)
                && after.Operand is MethodReference mr2
                && IsOldTile(mr2.DeclaringType.FullName);
            if (usedAsThis)
            {
                il.Remove(next);
            }
            else
            {
                next.OpCode = OpCodes.Ldobj;
                next.Operand = TileData(module);
                stats.ldobj++;
            }
        }
        else if (IsValueConsumer(next))
        {
            il.InsertBefore(next, Instruction.Create(OpCodes.Ldobj, TileData(module)));
            stats.ldobj++;
        }
    }

    static Instruction BuildStloc(MethodDefinition method, VariableDefinition local)
    {
        int idx = method.Body.Variables.IndexOf(local);
        return idx switch
        {
            0 => Instruction.Create(OpCodes.Stloc_0),
            1 => Instruction.Create(OpCodes.Stloc_1),
            2 => Instruction.Create(OpCodes.Stloc_2),
            3 => Instruction.Create(OpCodes.Stloc_3),
            _ => Instruction.Create(idx < byte.MaxValue ? OpCodes.Stloc_S : OpCodes.Stloc, local),
        };
    }

    public sealed class Stats
    {
        public int tiles, fields, accessors, methods, news, nulls, collections, ldobj;
    }
}
