// Auto-generated from OTAPI.Scripts/Mods/RefTile.Server.cs (TileSystemPatchLogic + MonoModCommon helpers),
// adapted for standalone plugin migration. Uses the same proven Tile->TileData conversion as the
// ref-tile game patch.
#pragma warning disable CS8321
#pragma warning disable CS0168
#pragma warning disable CS0436
using CallSite = Mono.Cecil.CallSite;
using ModFramework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MonoMod.Cil;
using MonoMod.Utils;
using OTAPI.UnifiedServerProcess;
using OTAPI.UnifiedServerProcess.Commons;
using OTAPI.UnifiedServerProcess.Extensions;
using OTAPI.UnifiedServerProcess.Optimize.LinkObjects;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;

namespace RefTile.PluginMigrator
{
/// <summary>Minimal stand-in for ModFwModder used by the ported TileSystemPatchLogic.</summary>
public sealed class ModuleContext(ModuleDefinition module)
{
    public ModuleDefinition Module { get; } = module;

    public void Log(string message) => Console.WriteLine("[migrator] " + message);
    public AssemblyNameReference RelinkModuleMapGet(string name) => null;

    /// <summary>
    /// Resolves a Terraria type by full name: first from the module itself, then from the
    /// referenced OTAPI/Terraria assembly (via the assembly resolver).
    /// </summary>
    public static TypeDefinition ResolveType(ModuleDefinition module, string fullName)
    {
        var local = module.GetType(fullName);
        if (local is not null) return local;
        foreach (var ar in module.AssemblyReferences)
        {
            if (ar.Name is not ("OTAPI" or "TerrariaServer" or "Terraria" or "TerrariaServerAPI" or "tModLoader")) continue;
            try
            {
                var asm = module.AssemblyResolver.Resolve(ar);
                if (asm is null) continue;
                var td = asm.MainModule.GetType(fullName);
                if (td is not null) return td;
            }
            catch { /* try the next reference */ }
        }
        // fall back to a non-resolved scan of all referenced assemblies
        foreach (var ar in module.AssemblyReferences)
        {
            try
            {
                var asm = module.AssemblyResolver.Resolve(ar);
                if (asm is null) continue;
                var td = asm.MainModule.GetType(fullName);
                if (td is not null) return td;
            }
            catch { }
        }
        throw new InvalidOperationException($"Could not resolve type {fullName} from the plugin's references. Pass -ref <OTAPI.dll> so the resolver can find the Terraria assembly.");
    }
}
class TileSystemPatchLogic
{
    readonly ModuleContext modder;

    readonly TypeDefinition tileTypeDef;
    readonly TypeDefinition refTileTypeDef;
    readonly TypeDefinition tileTypeOldDef;
    readonly TypeDefinition tileTypeImplDef;
    bool IsTileType(TypeReference type, bool handleByRef = true, bool includingOriginal = false) {
        if (type is ByReferenceType byReferenceType) {
            return handleByRef && IsTileType(byReferenceType.ElementType);
        }
        return type.FullName == tileTypeDef.FullName
            || type.FullName == refTileTypeDef.FullName
            || type.FullName == tileTypeOldDef.FullName
            || (includingOriginal && type.FullName == tileTypeImplDef.FullName);
    }

    readonly MethodDefinition tileCreate;
    readonly MethodDefinition tileCreateWithExistingTile;

    readonly MethodDefinition refTile_GetTempMDef;
    readonly MethodDefinition refTile_GetDataMDef;

    readonly TypeDefinition tileCollectionDef;
    readonly TypeReference tileCollectionDefOld;
    readonly MethodDefinition tileCollection_CreateMDef;
    readonly MethodDefinition tileCollection_getItemMDef;
    readonly MethodDefinition tileCollection_GetRefTileMDef;
    readonly FieldDefinition tileCollectionFieldDefInMain;

    readonly Dictionary<string, string> tileNameMap;


    public TileSystemPatchLogic(ModuleContext modder) {
        this.modder = modder;

        tileTypeDef = ModuleContext.ResolveType(modder.Module, "Terraria.TileData");
        refTileTypeDef = ModuleContext.ResolveType(modder.Module, "Terraria.RefTileData");
        // Plugins were compiled against the classic OTAPI where the tile was the ITile interface.
        // ITile no longer exists in the ref-tile build, so it is matched by a synthetic definition
        // (only its FullName is used). The vanilla Tile class is matched via tileTypeImplDef.
        tileTypeOldDef = SyntheticType("Terraria.ITile");
        tileTypeImplDef = ModuleContext.ResolveType(modder.Module, "Terraria.Tile");

        tileCreate = tileTypeDef.Methods.Single(x => x.Name == "New" && x.Parameters.Count == 0);
        tileCreateWithExistingTile = tileTypeDef.Methods.Single(x => x.Name == "New" && x.Parameters.Count == 1);

        refTile_GetTempMDef = refTileTypeDef.GetMethod("get_" + "Temporary");
        refTile_GetDataMDef = refTileTypeDef.GetMethod("get_" + "Data");

        tileCollectionDef = ModuleContext.ResolveType(modder.Module, "Terraria.TileCollection");
        tileCollection_CreateMDef = tileCollectionDef.GetMethod("Create");
        tileCollection_getItemMDef = tileCollectionDef.GetMethod("get_Item");
        tileCollection_GetRefTileMDef = tileCollectionDef.GetMethod("GetRefTile");

        tileCollectionFieldDefInMain = ModuleContext.ResolveType(modder.Module, "Terraria.Main").Fields.First(f => f.Name == "tile");
        tileCollectionDefOld = tileCollectionFieldDefInMain.FieldType;
        tileCollectionFieldDefInMain.FieldType = tileCollectionDef;

        string oldTileFullName = tileTypeOldDef.FullName;

        tileNameMap = new() {
            { oldTileFullName, tileTypeDef.FullName },
            { "Terraria.Tile", tileTypeDef.FullName },
            { tileCollectionDefOld.FullName, tileCollectionDef.FullName }
        };
    }

    /// <summary>A detached TypeDefinition used only for FullName matching (e.g. the old ITile).</summary>
    static TypeDefinition SyntheticType(string fullName) {
        int dot = fullName.LastIndexOf('.');
        return new TypeDefinition(fullName[..dot], fullName[(dot + 1)..],
            TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.Public);
    }

    public void Patch() {

        Replace_GenericParamAndArgs(modder);

        Analyze_ModifiedTileParameter(modder, out var modifiedTileParameters);

        Replace_TileCollection(modder);

        Replace_TileDelegate(modder);

        Adjust_MFWHMethods(modder, modifiedTileParameters);

        Adjust_MethodReturnTileRef(modder);

        Analyze_ComponentsNeedAdjust(modder, modifiedTileParameters, out var fieldShouldAdjust, out var methodShouldAdjust);

        string oldTileFullName = tileTypeOldDef.FullName;

        Dictionary<string, MethodDefinition> allMethods = modder.Module
            .GetAllTypes()
            .Where(t => !t.Name.OrdinalStartsWith("<>f__AnonymousType"))
            .SelectMany(x => x.Methods)
            // Vanilla contains conversion operators overloaded by return type (e.g.
            // FloatIntUnion::op_Implicit(FloatIntUnion) -> float / int), which produce the same
            // identifier since GetIdentifier() does not include the return type. Keep the first.
            .GroupBy(x => x.GetIdentifier())
            .ToDictionary(g => g.Key, g => g.First());

        Adjust_RelinkModifiedComponets(modder, modifiedTileParameters, allMethods, fieldShouldAdjust, methodShouldAdjust, out var fieldReferences, out var methodsReferences);

        var tileOperateMethodsArray = methodShouldAdjust.Values.ToArray();

        Adjust_RefFeature(modder, modifiedTileParameters, methodShouldAdjust, allMethods, tileOperateMethodsArray);
        Adjust_UseRefTileModel(modder, modifiedTileParameters, fieldReferences: fieldReferences, methodsReferences: methodsReferences, tileOperateMethodsArray);

        Adjust_RemainingOldTileReferences();

        Adjust_CleanupOldTile();
    }

    private void Adjust_CleanupOldTile() {
        // In direct ref-tile mode tileTypeOldDef == tileTypeImplDef == Terraria.Tile.
        // Keep the (now empty) class in the module so any residual Tile-typed metadata stays valid.
        if (tileTypeOldDef != tileTypeImplDef) {
            modder.Module.Types.Remove(tileTypeOldDef);
        }
        tileTypeImplDef.Interfaces.Clear();
        foreach (var method in tileTypeImplDef.Methods.ToArray()) {
            if (!method.IsStatic) {
                tileTypeImplDef.Methods.Remove(method);
            }
        }
        foreach (var field in tileTypeImplDef.Fields.ToArray()) {
            if (!field.IsStatic) {
                tileTypeImplDef.Fields.Remove(field);
            }
        }
        foreach (var prop in tileTypeImplDef.Properties.ToArray()) {
            if (prop.HasThis) {
                tileTypeImplDef.Properties.Remove(prop);
            }
        }
        tileTypeImplDef.Attributes |= TypeAttributes.Sealed;
        tileTypeImplDef.Attributes |= TypeAttributes.Abstract;
    }

    /// <summary>
    /// This project patches raw vanilla instead of an already-patched OTAPI build (like the
    /// OTAPI.Upcoming reference used by UnifiedServerProcess). The PreMerge mods remap the vanilla
    /// Tile class to the ITile interface, but some references survive in wrapped forms (array element
    /// types, local variables, ITile[,]::Get calls, ITile/Tile member references) that the regular
    /// relink passes do not catch. Convert every remaining ITile reference to TileData before the old
    /// interface is removed from the module, otherwise Cecil cannot write the assembly
    /// ("Member 'Terraria.ITile' is declared in another module...").
    /// </summary>
    private void Adjust_RemainingOldTileReferences() {
        var map = MonoModCommon.Structure.MapOption.Create(replaceType: [(tileTypeOldDef, tileTypeDef)]);

        foreach (var type in modder.Module.GetAllTypes()) {
            foreach (var field in type.Fields) {
                if (ReferencesOldTile(field.FieldType)) {
                    field.FieldType = MonoModCommon.Structure.DeepMapTypeReference(field.FieldType, map);
                }
            }
            foreach (var prop in type.Properties) {
                if (ReferencesOldTile(prop.PropertyType)) {
                    prop.PropertyType = MonoModCommon.Structure.DeepMapTypeReference(prop.PropertyType, map);
                }
            }
            foreach (var method in type.Methods) {
                bool methodTouched = false;

                if (ReferencesOldTile(method.ReturnType)) {
                    method.ReturnType = MonoModCommon.Structure.DeepMapTypeReference(method.ReturnType, map);
                    methodTouched = true;
                }
                foreach (var p in method.Parameters) {
                    if (ReferencesOldTile(p.ParameterType)) {
                        p.ParameterType = MonoModCommon.Structure.DeepMapTypeReference(p.ParameterType, map);
                        methodTouched = true;
                    }
                }
                if (method.HasGenericParameters) {
                    foreach (var gp in method.GenericParameters) {
                        foreach (var c in gp.Constraints) {
                            if (ReferencesOldTile(c.ConstraintType)) {
                                c.ConstraintType = MonoModCommon.Structure.DeepMapTypeReference(c.ConstraintType, map);
                                methodTouched = true;
                            }
                        }
                    }
                }
                if (!method.HasBody) {
                    continue;
                }
                foreach (var v in method.Body.Variables) {
                    if (ReferencesOldTile(v.VariableType)) {
                        v.VariableType = MonoModCommon.Structure.DeepMapTypeReference(v.VariableType, map);
                        methodTouched = true;
                    }
                }

                var jumpTargets = MonoModCommon.Stack.BuildJumpSitesMap(method);

                foreach (var inst in method.Body.Instructions) {
                    switch (inst.Operand) {
                        case TypeReference typeRef when ReferencesOldTile(typeRef):
                            inst.Operand = MonoModCommon.Structure.DeepMapTypeReference(typeRef, map);
                            methodTouched = true;
                            break;
                        case MethodReference methodRef:
                            var declaringFullName = methodRef.DeclaringType.FullName;
                            if (methodRef.DeclaringType is ArrayType arrayType && ReferencesOldTile(arrayType.ElementType)) {
                                // e.g. ITile[,]::Get -> TileData[,]::Get
                                inst.Operand = MonoModCommon.Structure.DeepMapMethodReference(methodRef, map);
                                methodTouched = true;
                            }
                            else if (declaringFullName == tileTypeOldDef.FullName || declaringFullName == tileTypeImplDef.FullName) {
                                // property accessor on the old interface/class -> direct field access on TileData
                                // (fall back to the TileData accessor method for property-backed members like collisionType)
                                if (methodRef.Name.OrdinalStartsWith("get_") || methodRef.Name.OrdinalStartsWith("set_")) {
                                    string fieldName = methodRef.Name.Substring(4);
                                    var field = tileTypeDef.FindField(fieldName);
                                    if (field is not null) {
                                        inst.OpCode = methodRef.Name.OrdinalStartsWith("get_") ? OpCodes.Ldfld : OpCodes.Stfld;
                                        inst.Operand = field;
                                    }
                                    else {
                                        inst.Operand = tileTypeDef.Methods.FirstOrDefault(m => m.Name == methodRef.Name && m.Parameters.Count == methodRef.Parameters.Count)
                                            ?? throw new NotSupportedException($"TileData accessor not found for {methodRef.FullName}");
                                    }
                                }
                                else {
                                    inst.Operand = tileTypeDef.Methods.FirstOrDefault(m => m.Name == methodRef.Name && m.Parameters.Count == methodRef.Parameters.Count)
                                        ?? throw new NotSupportedException($"TileData method not found for {methodRef.FullName}");
                                }
                                methodTouched = true;
                            }
                            break;
                        case FieldReference fieldRef:
                            if (ReferencesOldTile(fieldRef.FieldType)) {
                                // e.g. WallDrawing._tileArray : ITile[,] -> TileData[,]
                                inst.Operand = new FieldReference(fieldRef.Name,
                                    MonoModCommon.Structure.DeepMapTypeReference(fieldRef.FieldType, map),
                                    MonoModCommon.Structure.DeepMapTypeReference(fieldRef.DeclaringType, map));
                                methodTouched = true;
                            }
                            else if (fieldRef.DeclaringType.FullName == tileTypeOldDef.FullName || fieldRef.DeclaringType.FullName == tileTypeImplDef.FullName) {
                                inst.Operand = tileTypeDef.FindField(fieldRef.Name) ?? throw new NotSupportedException($"TileData field not found for {fieldRef.FullName}");
                                methodTouched = true;
                            }
                            break;
                    }

                    // After converting a class-based Tile[,] to the struct TileData[,], loading an
                    // element value must become "ldobj TileData" instead of "ldind.ref".
                    if (methodTouched && inst.OpCode == OpCodes.Ldind_Ref) {
                        try {
                            var argPaths = MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, inst, jumpTargets);
                            foreach (var argPath in argPaths) {
                                var paths = MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(method, argPath.ParametersSources[0].Instructions.Last(), jumpTargets);
                                foreach (var path in paths) {
                                    if (path.StackTopType is ByReferenceType byRef && byRef.ElementType.FullName == tileTypeDef.FullName) {
                                        inst.OpCode = OpCodes.Ldobj;
                                        inst.Operand = tileTypeDef;
                                        break;
                                    }
                                }
                                if (inst.OpCode != OpCodes.Ldind_Ref) {
                                    break;
                                }
                            }
                        }
                        catch {
                            // stack analysis is best-effort; leave the instruction untouched
                        }
                    }
                }
            }
        }
    }

    bool ReferencesOldTile(TypeReference type) {
        if (type is ArrayType arrayType) {
            return ReferencesOldTile(arrayType.ElementType);
        }
        if (type is ByReferenceType byRef) {
            return ReferencesOldTile(byRef.ElementType);
        }
        if (type is PointerType pointerType) {
            return ReferencesOldTile(pointerType.ElementType);
        }
        if (type is GenericInstanceType genericInstanceType) {
            foreach (var arg in genericInstanceType.GenericArguments) {
                if (ReferencesOldTile(arg)) {
                    return true;
                }
            }
            return false;
        }
        return type.FullName == tileTypeOldDef.FullName;
    }

    private void Adjust_UseRefTileModel(ModuleContext modder,
        Dictionary<string, HashSet<int>> modifiedTileParameters,
        Dictionary<string, Dictionary<string, MethodDefinition>> fieldReferences,
        Dictionary<string, Dictionary<string, MethodDefinition>> methodsReferences,
        MethodDefinition[] tileOperateMethodsArray) {

        Dictionary<string, FieldDefinition> refTileFields = [];
        Dictionary<string, MethodDefinition> visited = [];
        Dictionary<string, MethodDefinition> refTileTransferMethods = [];

        Dictionary<string, (MethodDefinition refVersion, bool applied)> retRefModelMethodMaps = new() {
            { tileCreate.GetIdentifier(), (refTile_GetTempMDef, true) },
            { tileCollectionDef.GetMethod("get_Item").GetIdentifier(), (tileCollection_GetRefTileMDef, true) }
        };
        // OTAPI.Hooks.Tile only exists when the old ITile hook mod was part of the pipeline;
        // in direct ref-tile mode it is absent, so its InvokeCreate map entry is optional.
        var otapiHooksTile = modder.Module.GetType("OTAPI.Hooks/Tile");
        if (otapiHooksTile is not null) {
            var invokeCreate = otapiHooksTile.Methods.FirstOrDefault(m => m.Name == "InvokeCreate" && m.Parameters.Count == 0);
            if (invokeCreate is not null) {
                retRefModelMethodMaps[invokeCreate.GetIdentifier()] = (refTile_GetTempMDef, true);
            }
        }

        foreach (var method in tileOperateMethodsArray) {

            if (!method.HasBody) {
                continue;
            }

            foreach (var inst in method.Body.Instructions) {
                if (!inst.MatchLdflda(out var field) || !IsTileType(field.FieldType)) {
                    continue;
                }
                var usage = MonoModCommon.Stack.TraceStackValueConsumers(method, inst);
                if (usage.Length != 1 || !usage[0].MatchCallOrCallvirt(out var methodReference) || !modifiedTileParameters.TryGetValue(methodReference.GetIdentifier(), out var indexes)) {
                    continue;
                }
                if (methodReference.Name.OrdinalStartsWith("mfwh_")) {
                    continue;
                }
                var fieldDef = field.Resolve();
                fieldDef.FieldType = refTileTypeDef;
                refTileFields.TryAdd(fieldDef.GetIdentifier(), fieldDef);
                visited.TryAdd(method.GetIdentifier(), method);
            }
        }
        Stack<MethodDefinition> works = new(visited.Values);
        visited.Clear();
        while (works.Count > 0) {
            var currentMethod = works.Pop();
            if (!visited.TryAdd(currentMethod.GetIdentifier(), currentMethod)) {
                continue;
            }
            if (!currentMethod.HasBody) {
                continue;
            }
            if (currentMethod.DeclaringType.GetRootDeclaringType().Namespace.OrdinalStartsWith("HookEvents.")) {
                continue;
            }
            var ilProcessor = currentMethod.Body.GetILProcessor();
            var jumpSites = MonoModCommon.Stack.BuildJumpSitesMap(currentMethod);
            HashSet<Instruction> skipArgmentOperations = [];
            Dictionary<int, TypeReference> paramOriginalType = [];
            Dictionary<int, VariableDefinition> localMap = [];

            Dictionary<string, MethodDefinition> usedFields = [];

            string currentMethodOldId = currentMethod.GetIdentifier();
            Instruction[] instArray = currentMethod.Body.Instructions.ToArray();
            for (int i = 0; i < instArray.Length; i++) {
                Instruction? inst = instArray[i];
                FieldDefinition? refTileFieldDef = null;
                if (inst.Operand is FieldReference fieldReference && refTileFields.TryGetValue(fieldReference.GetIdentifier(), out refTileFieldDef)) {
                    fieldReference.FieldType = refTileTypeDef;
                    if (fieldReferences.TryGetValue(fieldReference.GetIdentifier(), out var usedFieldMethods)) {
                        foreach (var kv in usedFieldMethods) {
                            usedFields.TryAdd(kv.Key, kv.Value);
                        }
                    }
                }
                switch (inst.OpCode.Code) {
                    case Code.Ldfld:
                        if (refTileFieldDef is null) {
                            break;
                        }
                        inst.OpCode = OpCodes.Ldflda;
                        ilProcessor.InsertAfter(inst, [
                            Instruction.Create(OpCodes.Call, refTile_GetDataMDef),
                            Instruction.Create(OpCodes.Ldobj),
                        ]);
                        break;
                    case Code.Ldflda:
                        if (refTileFieldDef is null) {
                            break;
                        }
                        ilProcessor.InsertAfter(inst, Instruction.Create(OpCodes.Call, refTile_GetDataMDef));
                        break;
                    case Code.Stfld:
                        if (refTileFieldDef is null) {
                            break;
                        }
                        foreach (var path in MonoModCommon.Stack.AnalyzeInstructionArgsSources(currentMethod, inst, jumpSites)) {
                            var loadValueBegin = path.ParametersSources[1].Instructions.First();
                            var loadValueEnd = path.ParametersSources[1].Instructions.Last();
                            Adjust_UseRefTileModel(path.ParametersSources[1], ref inst, loadValueBegin, loadValueEnd);
                        }
                        break;
                    case Code.Call:
                    case Code.Callvirt:
                    case Code.Newobj:
                        var callee = (MethodReference)inst.Operand!;
                        if (!refTileTransferMethods.TryGetValue(callee.GetIdentifier(), out var transferMethod)) {
                            break;
                        }
                        foreach (var path in MonoModCommon.Stack.AnalyzeParametersSources(currentMethod, inst, jumpSites)) {
                            for (int paramIndexInculdeThis = 0; paramIndexInculdeThis < path.ParametersSources.Length; paramIndexInculdeThis++) {
                                int paramIndexExculdeThis = paramIndexInculdeThis;
                                if (callee.HasThis && inst.OpCode.Code != Code.Newobj) {
                                    paramIndexExculdeThis -= 1;
                                }
                                if (transferMethod.Parameters[paramIndexExculdeThis].ParameterType.FullName != refTileTypeDef.FullName) {
                                    continue;
                                }

                                var loadValueBegin = path.ParametersSources[paramIndexInculdeThis].Instructions.First();
                                var loadValueEnd = path.ParametersSources[paramIndexInculdeThis].Instructions.Last();

                                Adjust_UseRefTileModel(path.ParametersSources[paramIndexInculdeThis], ref inst, loadValueBegin, loadValueEnd);
                            }
                        }
                        inst.Operand = MonoModCommon.Structure.DeepMapMethodReference(transferMethod, new());
                        break;
                }
            }
            foreach (var m in usedFields.Values) {
                works.Push(m);
            }
            Adjust_RefTileLocal();
            Adjust_RefTileParamAndIncrementAdd();

            continue;

            bool Adjust_UseRefTileModel<TSource>(TSource source, ref Instruction inst, Instruction loadValueBegin, Instruction loadValueEnd) where TSource : MonoModCommon.Stack.ArgumentSource {
                Instruction rawLoadTile = loadValueEnd;
                if (loadValueEnd.OpCode == OpCodes.Ldobj) {
                    var previous = loadValueEnd.Previous;
                    while (previous.OpCode.StackBehaviourPush == StackBehaviour.Push0) {
                        previous = previous.Previous;
                    }
                    rawLoadTile = previous;
                }
                if (MonoModCommon.IL.TryGetReferencedParameter(currentMethod, rawLoadTile, out var parameter)
                    && IsTileType(parameter.ParameterType)
                    && parameter.ParameterType.FullName != refTileTypeDef.FullName) {

                    paramOriginalType[parameter.Index] = parameter.ParameterType;
                    parameter.ParameterType = refTileTypeDef;
                    var newInst = MonoModCommon.IL.BuildParameterLoad(currentMethod, currentMethod.Body, parameter);
                    rawLoadTile.OpCode = newInst.OpCode;
                    rawLoadTile.Operand = newInst.Operand;

                    int skipCount = Array.IndexOf(source.Instructions, rawLoadTile) + 1;
                    if (skipCount == 0) {
                        throw new Exception();
                    }

                    foreach (var rest in source.Instructions.Skip(skipCount)) {
                        rest.OpCode = OpCodes.Nop;
                        rest.Operand = null;
                    }
                    skipArgmentOperations.Add(rawLoadTile);
                    return true;
                }
                else if (rawLoadTile.OpCode == OpCodes.Call || rawLoadTile.OpCode == OpCodes.Callvirt) {
                    string id = ((MethodReference)rawLoadTile.Operand).GetIdentifier();
                    if (retRefModelMethodMaps.TryGetValue(id, out var retRefModelMethod)) {
                        rawLoadTile.Operand = retRefModelMethod.refVersion;
                        if (!retRefModelMethod.applied) {
                            retRefModelMethod.refVersion.DeclaringType.Methods.Add(retRefModelMethod.refVersion);
                            retRefModelMethod.applied = true;
                            retRefModelMethodMaps[id] = retRefModelMethod;
                        }
                        return true;
                    }
                    switch (inst.OpCode.Code) {
                        case Code.Stloc_0:
                        case Code.Stloc_1:
                        case Code.Stloc_2:
                        case Code.Stloc_3:
                        case Code.Stloc_S:
                        case Code.Stloc:
                            var local = MonoModCommon.IL.GetReferencedVariable(currentMethod, inst);
                            var mappedLocal = localMap[local.Index];
                            ilProcessor.InsertAfter(rawLoadTile, [
                                MonoModCommon.IL.BuildVariableStore(currentMethod, currentMethod.Body, mappedLocal),
                                MonoModCommon.IL.BuildVariableLoadAddress(currentMethod, currentMethod.Body, mappedLocal),
                                Instruction.Create(OpCodes.Call, refTile_GetDataMDef),
                            ]);
                            break;
                    }
                }
                else if (MonoModCommon.IL.TryGetReferencedVariable(currentMethod, rawLoadTile, out var variable)
                    && IsTileType(variable.VariableType)
                    && variable.VariableType.FullName != refTileTypeDef.FullName) {

                    if (!localMap.TryGetValue(variable.Index, out var mappedLocal)) {
                        localMap[variable.Index] = mappedLocal = new VariableDefinition(refTileTypeDef);
                        currentMethod.Body.Variables.Add(mappedLocal);
                    }
                    var newInst = MonoModCommon.IL.BuildVariableLoad(currentMethod, currentMethod.Body, mappedLocal);
                    rawLoadTile.OpCode = newInst.OpCode;
                    rawLoadTile.Operand = newInst.Operand;

                    int skipCount = Array.IndexOf(source.Instructions, rawLoadTile) + 1;
                    if (skipCount == 0) {
                        throw new Exception();
                    }

                    foreach (var rest in source.Instructions.Skip(skipCount)) {
                        rest.OpCode = OpCodes.Nop;
                        rest.Operand = null;
                    }
                    return true;
                }
                return false;
            }

            void Adjust_RefTileLocal() {
                if (localMap.Count == 0) {
                    return;
                }
                bool anyLocalModified = false;
                do {
                    anyLocalModified = false;
                    Instruction[] array = [.. currentMethod.Body.Instructions];

                    for (int i = 0; i < array.Length; i++) {
                        var inst = array[i];
                        if (!MonoModCommon.IL.TryGetReferencedVariable(currentMethod, inst, out var local) || !localMap.TryGetValue(local.Index, out var mappedLocal)) {
                            continue;
                        }
                        switch (inst.OpCode.Code) {
                            case Code.Stloc_0:
                            case Code.Stloc_1:
                            case Code.Stloc_2:
                            case Code.Stloc_3:
                            case Code.Stloc_S:
                            case Code.Stloc:
                                foreach (var path in MonoModCommon.Stack.AnalyzeInstructionArgsSources(currentMethod, inst, jumpSites)) {
                                    var loadValueBegin = path.ParametersSources[0].Instructions.First();
                                    var loadValueEnd = path.ParametersSources[0].Instructions.Last();
                                    if (Adjust_UseRefTileModel(path.ParametersSources[0], ref inst, loadValueBegin, loadValueEnd)) {
                                        anyLocalModified = true;
                                    }
                                }
                                break;
                        }
                    }
                }
                while (anyLocalModified);
            }

            void Adjust_RefTileParamAndIncrementAdd() {
                if (!currentMethod.Parameters.Any(p => p.ParameterType.FullName == refTileTypeDef.FullName)) {
                    return;
                }

                visited.TryAdd(currentMethod.GetIdentifier(), currentMethod);
                refTileTransferMethods[currentMethodOldId] = currentMethod;

                Instruction[] array = [.. currentMethod.Body.Instructions];

                for (int i = 0; i < array.Length; i++) {
                    var inst = array[i];
                    if (!MonoModCommon.IL.TryGetReferencedParameter(currentMethod, inst, out var parameter) || parameter.ParameterType.FullName != refTileTypeDef.FullName) {
                        continue;
                    }
                    var originalType = paramOriginalType[parameter.Index];
                    if (skipArgmentOperations.Contains(inst)) {
                        continue;
                    }
                    switch (inst.OpCode.Code) {
                        case Code.Ldarg_0:
                        case Code.Ldarg_1:
                        case Code.Ldarg_2:
                        case Code.Ldarg_3:
                        case Code.Ldarg_S:
                        case Code.Ldarg:
                            var tmp = MonoModCommon.IL.BuildParameterLoadAddress(currentMethod, currentMethod.Body, parameter);
                            inst.OpCode = tmp.OpCode;
                            if (originalType is ByReferenceType) {
                                ilProcessor.InsertBeforeSeamlessly(ref inst, Instruction.Create(OpCodes.Call, refTile_GetDataMDef));
                            }
                            else {
                                ilProcessor.InsertBeforeSeamlessly(ref inst, [
                                    Instruction.Create(OpCodes.Call, refTile_GetDataMDef),
                                Instruction.Create(OpCodes.Ldobj),
                            ]);
                            }
                            break;
                        case Code.Ldarga_S:
                        case Code.Ldarga:
                            ilProcessor.InsertBeforeSeamlessly(ref inst, Instruction.Create(OpCodes.Call, refTile_GetDataMDef));
                            break;
                        case Code.Starg_S:
                        case Code.Starg:
                            throw new NotImplementedException();
                        default:
                            break;
                    }
                }
                if (methodsReferences.TryGetValue(currentMethodOldId, out var callers)) {
                    foreach (var caller in callers.Values) {
                        works.Push(caller);
                    }
                }

                return;
            }
        }
    }

    private void Adjust_RefFeature(ModuleContext modder,
        Dictionary<string, HashSet<int>> modifiedTileParameters,
        Dictionary<string, MethodDefinition> methodShouldAdjust,
        Dictionary<string, MethodDefinition> allMethods,
        MethodDefinition[] tileOperateMethodsArray) {

        int progress = 0;
        foreach (var method in tileOperateMethodsArray) {

            if (!method.HasBody) {
                continue;
            }

            var jumpTargets = MonoModCommon.Stack.BuildJumpSitesMap(method);

            progress += 1;

            Console.WriteLine($"[{progress}/{methodShouldAdjust.Count}] Adjusting Tile null handling in method: {method.GetDebugName()}");

            var iLProcessor = method.Body.GetILProcessor();

            EachMethod_Analyze_WillModifyLocals(method, modifiedTileParameters, jumpTargets, out var notReadonlyVariables);

            Console.WriteLine($"Identified {notReadonlyVariables.Count} non-readonly TileData variables in method: {method.GetDebugName()} - {string.Join(", ", notReadonlyVariables.Select(x => "v" + x.Index))}");

            EachMethod_Adjust_MakeRefModifiedLocals(method, jumpTargets, notReadonlyVariables);

            void EachMethod_Adjust_StoreValueToAddress() {
                foreach (var instruction in method.Body.Instructions.ToArray()) {
                    if (instruction.OpCode == OpCodes.Stind_Ref) {
                        var sourcePaths = MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, jumpTargets)
                            .SelectMany(p => MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(method, p.ParametersSources[0].Instructions.Last(), jumpTargets));

                        foreach (var path in sourcePaths) {

                            var loadBegin = path.Instructions.First();
                            TypeReference? type = null;
                            switch (loadBegin.OpCode.Code) {
                                case Code.Call:
                                case Code.Callvirt:
                                    var calleeRef = (MethodReference)loadBegin.Operand!;
                                    type = calleeRef.ReturnType;
                                    break;
                                case Code.Ldflda:
                                case Code.Ldsflda:
                                    var field = (FieldReference)loadBegin.Operand!;
                                    type = field.FieldType;
                                    break;
                                case Code.Ldarg_0:
                                case Code.Ldarg_1:
                                case Code.Ldarg_2:
                                case Code.Ldarg_3:
                                case Code.Ldarg_S:
                                case Code.Ldarg:
                                case Code.Ldarga_S:
                                case Code.Ldarga:
                                    if (MonoModCommon.IL.TryGetReferencedParameter(method, loadBegin, out var p)) {
                                        type = p.ParameterType;
                                    }
                                    break;
                                case Code.Ldloc_0:
                                case Code.Ldloc_1:
                                case Code.Ldloc_2:
                                case Code.Ldloc_3:
                                case Code.Ldloc_S:
                                case Code.Ldloc:
                                case Code.Ldloca_S:
                                case Code.Ldloca:
                                    if (MonoModCommon.IL.TryGetReferencedVariable(method, loadBegin, out var v)) {
                                        type = v.VariableType;
                                    }
                                    break;
                            }
                            if (type != null && IsTileType(type)) {
                                instruction.OpCode = OpCodes.Stobj;
                                instruction.Operand = tileTypeDef;
                                break;
                            }
                        }
                    }
                }
            }

            void EachMethod_Adjust_VariableDefinitionType() {
                foreach (var instruction in method.Body.Instructions.ToArray()) {
                    switch (instruction.OpCode.Code) {
                        case Code.Stloc:
                        case Code.Stloc_S:
                        case Code.Stloc_0:
                        case Code.Stloc_1:
                        case Code.Stloc_2:
                        case Code.Stloc_3:

                            var varRef = MonoModCommon.IL.GetReferencedVariable(method, instruction);
                            if (!IsTileType(varRef.VariableType)) {
                                continue;
                            }

                            var paths = MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, jumpTargets)
                                .SelectMany(p => MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(method, p.ParametersSources[0].Instructions.Last(), jumpTargets))
                                .ToHashSet();

                            if (varRef.VariableType is ByReferenceType) {
                                foreach (var path in paths) {
                                    if (path.StackTopType is ByReferenceType) {
                                        continue;
                                    }

                                    var topInstruction = path.Instructions.First();
                                    switch (topInstruction.OpCode.Code) {
                                        case Code.Ldnull:
                                            topInstruction.OpCode = OpCodes.Call;
                                            topInstruction.Operand = tileTypeDef.GetMethod("get_" + "NULLREF");
                                            break;
                                        case Code.Ldarg_0:
                                        case Code.Ldarg_1:
                                        case Code.Ldarg_2:
                                        case Code.Ldarg_3:
                                        case Code.Ldarg_S:
                                        case Code.Ldarg:
                                            var parameter = MonoModCommon.IL.GetReferencedParameter(method, topInstruction);
                                            if (!IsTileType(parameter.ParameterType)) {
                                                break;
                                            }
                                            parameter.ParameterType = tileTypeDef.MakeByReferenceType();
                                            break;
                                        case Code.Ldloc_0:
                                        case Code.Ldloc_1:
                                        case Code.Ldloc_2:
                                        case Code.Ldloc_3:
                                        case Code.Ldloc_S:
                                        case Code.Ldloc:
                                            var variable = MonoModCommon.IL.GetReferencedVariable(method, topInstruction);
                                            variable.VariableType = tileTypeDef.MakeByReferenceType();
                                            break;
                                        case Code.Ldfld:
                                            topInstruction.OpCode = OpCodes.Ldflda;
                                            break;
                                        case Code.Ldsfld:
                                            topInstruction.OpCode = OpCodes.Ldsflda;
                                            break;
                                        case Code.Call:
                                        case Code.Callvirt:
                                            var calleeRef = (MethodReference)topInstruction.Operand!;
                                            if (calleeRef.Name == "InvokeCreate" && calleeRef.DeclaringType.FullName == "OTAPI.Hooks/Tile") {
                                                topInstruction.OpCode = OpCodes.Call;
                                                if (calleeRef.Parameters.Count == 0) {
                                                    topInstruction.OpCode = OpCodes.Call;
                                                    topInstruction.Operand = tileTypeDef.GetMethod("get_" + "EMPTYREF");
                                                }
                                                else {
                                                    topInstruction.OpCode = OpCodes.Call;
                                                    topInstruction.Operand = tileTypeDef.Methods.First(m =>
                                                    m.Parameters.Count == calleeRef.Parameters.Count &&
                                                    m.Name == "GetEMPTYREF");
                                                }
                                            }
                                            break;
                                    }
                                }
                            }
                            else {
                                HashSet<Instruction> visited = [];

                                if (paths.Count == 1) {
                                    var path = paths.First();
                                    if (path.Instructions.Length == 1 && path.Instructions[0].OpCode == OpCodes.Call) {
                                        var call = (MethodReference)path.Instructions[0].Operand!;
                                        if (call.GetIdentifier() == tileTypeDef.GetMethod("get_" + "NULLREF").GetIdentifier()) {
                                            path.Instructions[0].OpCode = OpCodes.Ldsfld;
                                            path.Instructions[0].Operand = tileTypeDef.Fields.Single(f => f.Name == "NULL");
                                            break;
                                        }
                                    }
                                }

                                foreach (var path in paths) {
                                    if (path.StackTopType is ByReferenceType referenceType) {
                                        var loadEnd = path.Instructions.Last();
                                        if (visited.Add(loadEnd)) {

                                            List<Instruction> inserts = [];

                                            if (loadEnd.OpCode.OperandType is OperandType.ShortInlineBrTarget or OperandType.InlineSwitch) {
                                                var loadObj = Instruction.Create(OpCodes.Ldobj, referenceType.ElementType);

                                                inserts.Add(Instruction.Create(OpCodes.Br, loadEnd.Next));
                                                inserts.Add(loadObj);
                                                if (loadEnd.Operand is Instruction target) {
                                                    inserts.Add(Instruction.Create(OpCodes.Br, target));
                                                    loadEnd.Operand = loadObj;
                                                }
                                                else if (loadEnd.Operand is Instruction[] targets) {
                                                    inserts.Add(Instruction.Create(OpCodes.Switch, targets));
                                                    for (int i = 0; i < targets.Length; i++) {
                                                        if (targets[i] == instruction) {
                                                            targets[i] = loadObj;
                                                        }
                                                    }
                                                }

                                                // update jump targets
                                                jumpTargets = MonoModCommon.Stack.BuildJumpSitesMap(method);
                                            }
                                            else {
                                                inserts.Add(Instruction.Create(OpCodes.Ldobj, referenceType.ElementType));
                                            }

                                            iLProcessor.InsertAfter(loadEnd, inserts);
                                        }
                                    }
                                }
                            }
                            break;
                    }
                }
            }

            void EachMethod_Adjust_NullCheck() {
                void NullLoad(Instruction insertedNotNullCheck) {
                    var argPaths = MonoModCommon.Stack.AnalyzeParametersSources(method, insertedNotNullCheck, jumpTargets);
                    foreach (var argPath in argPaths) {
                        var paths = MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(method, argPath.ParametersSources[0].Instructions.Last(), jumpTargets);
                        foreach (var path in paths) {
                            if (path.StackTopType is null) {
                                foreach (var inst in path.Instructions) {
                                    if (inst.OpCode == OpCodes.Ldnull) {
                                        inst.OpCode = OpCodes.Ldsfld;
                                        inst.Operand = tileTypeDef.Fields.Single(f => f.Name == "NULL");
                                    }
                                }
                            }
                        }
                    }
                }
                foreach (var instruction in method.Body.Instructions.ToArray()) {
                    switch (instruction.OpCode.Code) {
                        // Jump if v1 is true
                        case Code.Brtrue:
                        case Code.Brtrue_S:
                        case Code.Brfalse:
                        case Code.Brfalse_S: {
                                var path = MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, jumpTargets).First();
                                var type = MonoModCommon.Stack.AnalyzeStackTopType(method, path.ParametersSources[0].Instructions.Last(), jumpTargets);
                                if (type is not null && IsTileType(type)) {
                                    var isNotNullCall = Instruction.Create(OpCodes.Call, tileTypeDef.FindMethod("get_" + "IsNotNull"));
                                    iLProcessor.InsertBefore(instruction, isNotNullCall);
                                    NullLoad(isNotNullCall);
                                }
                                break;
                            }
                        // True if v1 equals v2
                        case Code.Ceq: {
                                foreach (var path in MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, jumpTargets)) {
                                    var typeV1 = MonoModCommon.Stack.AnalyzeStackTopType(method, path.ParametersSources[0].Instructions.Last(), jumpTargets);
                                    var typeV2 = MonoModCommon.Stack.AnalyzeStackTopType(method, path.ParametersSources[1].Instructions.Last(), jumpTargets);

                                    if ((typeV1 is null && typeV2 is not null && IsTileType(typeV2))) {
                                        foreach (var instV1 in path.ParametersSources[0].Instructions) {
                                            instV1.OpCode = OpCodes.Nop;
                                            instV1.Operand = null;
                                        }
                                        instruction.OpCode = OpCodes.Call;
                                        instruction.Operand = tileTypeDef.FindMethod("get_" + "IsNull");
                                        NullLoad(instruction);
                                    }
                                    else if (typeV2 is null && typeV1 is not null && IsTileType(typeV1)) {
                                        foreach (var instV2 in path.ParametersSources[1].Instructions) {
                                            instV2.OpCode = OpCodes.Nop;
                                            instV2.Operand = null;
                                        }
                                        instruction.OpCode = OpCodes.Call;
                                        instruction.Operand = tileTypeDef.FindMethod("get_" + "IsNull");
                                        NullLoad(instruction);
                                    }
                                }
                                break;
                            }
                        // True if v1 is greater than v2
                        case Code.Cgt_Un: {
                                foreach (var path in MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, jumpTargets)) {
                                    var typeV1 = MonoModCommon.Stack.AnalyzeStackTopType(method, path.ParametersSources[0].Instructions.Last(), jumpTargets);
                                    var typeV2 = MonoModCommon.Stack.AnalyzeStackTopType(method, path.ParametersSources[1].Instructions.Last(), jumpTargets);
                                    if (typeV2 is null && typeV1 is not null && IsTileType(typeV1)) {
                                        foreach (var instV2 in path.ParametersSources[1].Instructions) {
                                            instV2.OpCode = OpCodes.Nop;
                                            instV2.Operand = null;
                                        }
                                        instruction.OpCode = OpCodes.Call;
                                        instruction.Operand = tileTypeDef.FindMethod("get_" + "IsNotNull");
                                        NullLoad(instruction);
                                    }
                                }
                                break;
                            }
                    }
                }
            }

            void EachMethod_Adjust_LoadAddress() {

                foreach (var instruction in method.Body.Instructions.ToArray()) {

                    HashSet<MonoModCommon.Stack.StackTopTypePath> workPaths = [];
                    HashSet<MonoModCommon.Stack.StackTopTypePath> visited = [];

                    switch (instruction.OpCode.Code) {
                        case Code.Call:
                        case Code.Callvirt:
                            var mRef = (MethodReference)instruction.Operand;
                            if (IsTileType(mRef.DeclaringType, false, true) && mRef.HasThis) {
                                instruction.OpCode = OpCodes.Call;
                                foreach (var path in MonoModCommon.Stack.AnalyzeParametersSources(method, instruction, jumpTargets)) {
                                    foreach (var inst in path.ParametersSources[0].Instructions) {
                                        if (inst.OpCode == OpCodes.Ldind_Ref) {
                                            method.Body.Instructions.Remove(inst);
                                        }
                                    }
                                }
                            }
                            break;
                        case Code.Stfld:
                        case Code.Ldfld:
                        case Code.Ldflda:
                            var fRef = (FieldReference)instruction.Operand;
                            if (IsTileType(fRef.DeclaringType, false, true) && tileTypeDef.Fields.FirstOrDefault(f => f.Name == fRef.Name) is { IsStatic: false }) {
                                foreach (var path in MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, jumpTargets)) {
                                    foreach (var inst in path.ParametersSources[0].Instructions) {
                                        if (inst.OpCode == OpCodes.Ldind_Ref) {
                                            method.Body.Instructions.Remove(inst);
                                        }
                                    }
                                }
                            }
                            break;
                    }

                    int sourceIndexOfLoadTileRef = -1;
                    switch (instruction.OpCode.Code) {
                        case Code.Callvirt:
                        case Code.Call:
                        case Code.Newobj: {
                                var calleeRef = (MethodReference)instruction.Operand!;

                                if ((calleeRef.DeclaringType is GenericInstanceType || calleeRef is GenericInstanceMethod) &&
                                    calleeRef.Parameters.Any(p => p.ParameterType is ByReferenceType { ElementType: GenericParameter })) {

                                    var methodCallPaths = MonoModCommon.Stack.AnalyzeParametersSources(method, instruction, jumpTargets);

                                    for (int i = 0; i < calleeRef.Parameters.Count; i++) {
                                        if (calleeRef.Parameters[i].ParameterType is ByReferenceType { ElementType: GenericParameter gp }) {
                                            static GenericInstanceType FindGenericInstanceType(TypeReference declaring, GenericParameter gpOfType) {
                                                var t = (TypeReference)gpOfType.Owner;
                                                while (declaring is not GenericInstanceType git || git.ElementType.FullName != t.FullName) {
                                                    declaring = declaring.DeclaringType;
                                                }
                                                return (GenericInstanceType)declaring;
                                            }
                                            var ga = gp.Owner switch {
                                                MethodReference m => ((GenericInstanceMethod)calleeRef).GenericArguments[gp.Position],
                                                TypeReference t => FindGenericInstanceType(calleeRef.DeclaringType, gp).GenericArguments[gp.Position],
                                                _ => throw new InvalidOperationException()
                                            };

                                            if (IsTileType(ga, false, true)) {

                                                foreach (var methodCallPath in methodCallPaths) {
                                                    var paths = MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(
                                                        method,
                                                        methodCallPath.ParametersSources[i + (calleeRef.HasThis ? 1 : 0)].Instructions.Last(),
                                                        jumpTargets);

                                                    foreach (var path in paths) {
                                                        workPaths.Add(path);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                if (allMethods.TryGetValue(calleeRef.GetIdentifier(), out var calleeDef)) {

                                    if (!calleeDef.Parameters.Any(p => IsTileType(p.ParameterType))) {

                                        if (!IsTileType(calleeDef.DeclaringType) || !calleeDef.HasThis) {
                                            break;
                                        }

                                        if (instruction.OpCode == OpCodes.Newobj) {
                                            break;
                                        }
                                    }

                                    var methodCallPaths = MonoModCommon.Stack.AnalyzeParametersSources(method, instruction, jumpTargets);

                                    if (modifiedTileParameters.TryGetValue(calleeDef.GetIdentifier(), out var theseParametersWillBeEdit)) {
                                        foreach (var methodCallPath in methodCallPaths) {
                                            foreach (int pIndex in theseParametersWillBeEdit) {
                                                var paths = MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(
                                                    method,
                                                    methodCallPath.ParametersSources[pIndex].Instructions.Last(),
                                                    jumpTargets);

                                                foreach (var path in paths) {
                                                    workPaths.Add(path);
                                                }
                                            }
                                        }
                                    }

                                    theseParametersWillBeEdit ??= [];

                                    // When calling an instance method of a struct, the 'this' Parameter needs to load the reference address, not the value itself
                                    if (calleeDef.HasThis && instruction.OpCode != OpCodes.Newobj && IsTileType(calleeDef.DeclaringType)) {

                                        // Avoid repeating the previous logic
                                        if (theseParametersWillBeEdit.Contains(0)) {
                                            break;
                                        }

                                        foreach (var methodCallPath in methodCallPaths) {
                                            var paths = MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(
                                                method,
                                                methodCallPath.ParametersSources[0].Instructions.Last(),
                                                jumpTargets);

                                            foreach (var path in paths) {
                                                workPaths.Add(path);
                                            }
                                        }
                                    }
                                }
                                break;
                            }
                        case Code.Stloc_0:
                        case Code.Stloc_1:
                        case Code.Stloc_2:
                        case Code.Stloc_3:
                        case Code.Stloc_S:
                        case Code.Stloc: {
                                var variable = MonoModCommon.IL.GetReferencedVariable(method, instruction);
                                if (variable.VariableType is ByReferenceType referenceType && IsTileType(referenceType.ElementType)) {
                                    sourceIndexOfLoadTileRef = 0;
                                }
                                break;
                            }
                        case Code.Ldfld: {
                                var field = (FieldReference)instruction.Operand!;
                                if (IsTileType(field.DeclaringType)) {
                                    sourceIndexOfLoadTileRef = 0;
                                }
                                break;
                            }
                        case Code.Stsfld: {
                                var field = (FieldReference)instruction.Operand!;
                                if (field.FieldType is ByReferenceType referenceType && IsTileType(referenceType.ElementType)) {
                                    sourceIndexOfLoadTileRef = 0;
                                }
                                break;
                            }
                        case Code.Stfld: {
                                var field = (FieldReference)instruction.Operand!;
                                if (IsTileType(field.DeclaringType)) {
                                    sourceIndexOfLoadTileRef = 0;
                                }
                                else if (field.FieldType is ByReferenceType referenceType && IsTileType(referenceType.ElementType)) {
                                    sourceIndexOfLoadTileRef = 1;
                                }
                                break;
                            }
                        case Code.Initobj:
                        case Code.Ldobj: {
                                var type = (TypeReference)instruction.Operand!;
                                if (IsTileType(type)) {
                                    sourceIndexOfLoadTileRef = 0;
                                }
                                break;
                            }
                    }

                    if (sourceIndexOfLoadTileRef != -1) {
                        foreach (var executePath in MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, jumpTargets)) {
                            var loadTileRef = executePath.ParametersSources[sourceIndexOfLoadTileRef].Instructions.Last();

                            var paths = MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(
                                method,
                                loadTileRef,
                                jumpTargets);

                            foreach (var path in paths) {
                                workPaths.Add(path);
                            }
                        }
                    }

                    foreach (var loadPath in workPaths) {

                        if (visited.Add(loadPath)) {

                            var loadBegin = loadPath.Instructions.First();
                            var loadEnd = loadPath.Instructions.Last();

                            switch (loadBegin.OpCode.Code) {
                                case Code.Ldnull:
                                    loadBegin.OpCode = OpCodes.Call;
                                    loadBegin.Operand = tileTypeDef.GetMethod("get_" + "NULLREF");
                                    break;
                                case Code.Call:
                                case Code.Callvirt:
                                case Code.Ldfld:
                                case Code.Ldsfld:

                                    if (workPaths.Count == 1 && (loadEnd.OpCode == OpCodes.Dup || MonoModCommon.Stack.TraceStackValueConsumers(method, loadBegin).Length == 1)) {
                                        loadEnd = loadBegin;
                                    }

                                    var calleeRef = loadBegin.Operand as MethodReference;
                                    var loadField = loadBegin.Operand as FieldReference;

                                    if (loadField is not null && loadField.DeclaringType.FullName == tileTypeDef.FullName && loadField.Name == "NULL") {
                                        loadBegin.OpCode = OpCodes.Call;
                                        loadBegin.Operand = tileTypeDef.GetMethod("get_" + "NULLREF");
                                    }
                                    else if (loadField is not null && !loadField.Resolve().IsInitOnly) {
                                        if (loadBegin.OpCode == OpCodes.Ldfld) {
                                            loadBegin.OpCode = OpCodes.Ldflda;
                                        }
                                        else if (loadBegin.OpCode == OpCodes.Ldsfld) {
                                            loadBegin.OpCode = OpCodes.Ldsflda;
                                        }
                                    }
                                    else if (loadField is not null || (calleeRef is not null && calleeRef.ReturnType is not ByReferenceType)) {
                                        var variable = new VariableDefinition(tileTypeDef);
                                        method.Body.Variables.Add(variable);
                                        var setLocal = MonoModCommon.IL.BuildVariableStore(method, method.Body, variable);
                                        var loadLocalAddress = MonoModCommon.IL.BuildVariableLoadAddress(method, method.Body, variable);

                                        List<Instruction> inserts = [];

                                        if (loadEnd.OpCode.OperandType is OperandType.ShortInlineBrTarget or OperandType.InlineSwitch) {

                                            inserts.Add(Instruction.Create(OpCodes.Br, loadEnd.Next));
                                            inserts.Add(setLocal);
                                            inserts.Add(loadLocalAddress);
                                            if (loadEnd.Operand is Instruction target) {
                                                inserts.Add(Instruction.Create(OpCodes.Br, target));
                                                loadEnd.Operand = setLocal;
                                            }
                                            else if (loadEnd.Operand is Instruction[] targets) {
                                                inserts.Add(Instruction.Create(OpCodes.Switch, targets));
                                                for (int i = 0; i < targets.Length; i++) {
                                                    if (targets[i] == instruction) {
                                                        targets[i] = setLocal;
                                                    }
                                                }
                                            }

                                            // update jump targets
                                            jumpTargets = MonoModCommon.Stack.BuildJumpSitesMap(method);
                                        }
                                        else {
                                            inserts.Add(setLocal);
                                            inserts.Add(loadLocalAddress);
                                        }

                                        iLProcessor.InsertAfter(loadEnd, inserts);
                                    }
                                    break;
                                case Code.Ldarg:
                                case Code.Ldarg_S:
                                case Code.Ldarg_0:
                                case Code.Ldarg_1:
                                case Code.Ldarg_2:
                                case Code.Ldarg_3:
                                    if (MonoModCommon.IL.TryGetReferencedParameter(method, loadBegin, out var p)) {
                                        if (p.ParameterType is not ByReferenceType) {
                                            var inst = MonoModCommon.IL.BuildParameterLoadAddress(method, method.Body, p);
                                            loadBegin.OpCode = inst.OpCode;
                                            loadBegin.Operand = inst.Operand;
                                        }
                                    }
                                    break;
                                case Code.Ldarga:
                                case Code.Ldarga_S:
                                    if (MonoModCommon.IL.TryGetReferencedParameter(method, loadBegin, out var p_r)) {
                                        if (p_r.ParameterType is ByReferenceType) {
                                            var inst = MonoModCommon.IL.BuildParameterLoad(method, method.Body, p_r);
                                            loadBegin.OpCode = inst.OpCode;
                                            loadBegin.Operand = inst.Operand;
                                        }
                                    }
                                    break;
                                case Code.Ldloc:
                                case Code.Ldloc_S:
                                case Code.Ldloc_0:
                                case Code.Ldloc_1:
                                case Code.Ldloc_2:
                                case Code.Ldloc_3:
                                    if (MonoModCommon.IL.TryGetReferencedVariable(method, loadBegin, out var v)) {
                                        if (v.VariableType is not ByReferenceType) {
                                            var inst = MonoModCommon.IL.BuildVariableLoadAddress(method, method.Body, v);
                                            loadBegin.OpCode = inst.OpCode;
                                            loadBegin.Operand = inst.Operand;
                                        }
                                    }
                                    break;
                                case Code.Ldloca_S:
                                case Code.Ldloca:
                                    if (MonoModCommon.IL.TryGetReferencedVariable(method, loadBegin, out var v_r)) {
                                        if (v_r.VariableType is ByReferenceType) {
                                            var inst = MonoModCommon.IL.BuildVariableLoad(method, method.Body, v_r);
                                            loadBegin.OpCode = inst.OpCode;
                                            loadBegin.Operand = inst.Operand;
                                        }
                                    }
                                    break;
                            }
                        }
                    }
                }
            }

            void EachMethod_Adjust_LoadObjValue() {
                foreach (var instruction in method.Body.Instructions.ToArray()) {

                    HashSet<MonoModCommon.Stack.StackTopTypePath> workPaths = [];
                    HashSet<MonoModCommon.Stack.StackTopTypePath> visited = [];

                    int index = -1;
                    switch (instruction.OpCode.Code) {
                        case Code.Callvirt:
                        case Code.Call: {

                                var calleeRef = (MethodReference)instruction.Operand!;

                                if (allMethods.TryGetValue(calleeRef.GetIdentifier(), out var calleeDef)) {

                                    if (!calleeDef.Parameters.Any(p => IsTileType(p.ParameterType)) && !(IsTileType(calleeDef.DeclaringType) && calleeDef.HasThis)) {
                                        break;
                                    }

                                    var methodCallPaths = MonoModCommon.Stack.AnalyzeParametersSources(method, instruction, jumpTargets);

                                    modifiedTileParameters.TryGetValue(calleeDef.GetIdentifier(), out var theseParametersWillBeEdit);
                                    theseParametersWillBeEdit ??= [];
                                    for (int i = 0; i < calleeDef.Parameters.Count; i++) {
                                        int pIndex = i + (calleeDef.HasThis ? 1 : 0);

                                        if (IsTileType(calleeDef.Parameters[i].ParameterType) && !(theseParametersWillBeEdit?.Contains(pIndex) ?? false)) {

                                            foreach (var methodCallPath in MonoModCommon.Stack.AnalyzeParametersSources(method, instruction, jumpTargets)) {
                                                var paths = MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(
                                                    method,
                                                    methodCallPath.ParametersSources[pIndex].Instructions.Last(),
                                                    jumpTargets);

                                                foreach (var path in paths) {
                                                    workPaths.Add(path);
                                                }
                                            }
                                        }
                                    }
                                }
                                break;
                            }
                        case Code.Stloc_0:
                        case Code.Stloc_1:
                        case Code.Stloc_2:
                        case Code.Stloc_3:
                        case Code.Stloc_S:
                        case Code.Stloc: {
                                var variable = MonoModCommon.IL.GetReferencedVariable(method, instruction);
                                if (variable.VariableType is not ByReferenceType && IsTileType(variable.VariableType)) {
                                    // 0. push value
                                    index = 0;
                                }
                                break;
                            }
                        case Code.Starg:
                        case Code.Starg_S: {
                                var parameter = MonoModCommon.IL.GetReferencedParameter(method, instruction);
                                if (parameter.ParameterType is not ByReferenceType && IsTileType(parameter.ParameterType)) {
                                    // 0. push value
                                    index = 0;
                                }
                                break;
                            }
                        case Code.Stsfld: {
                                var field = (FieldReference)instruction.Operand!;
                                if (field.FieldType is not ByReferenceType referenceType && IsTileType(field.FieldType)) {
                                    // 0. push value
                                    index = 0;
                                }
                                break;
                            }
                        case Code.Stfld: {
                                var field = (FieldReference)instruction.Operand!;
                                if (field.FieldType is not ByReferenceType referenceType && IsTileType(field.FieldType)) {
                                    // 0. push instance
                                    // 1. push value
                                    index = 1;
                                }
                                break;
                            }
                        case Code.Stobj: {
                                var type = (TypeReference)instruction.Operand!;
                                if (IsTileType(type)) {
                                    // 0. push address
                                    // 1. push value
                                    index = 1;
                                }
                                break;
                            }
                    }

                    if (index != -1) {
                        foreach (var fieldPath in MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, jumpTargets)) {
                            var paths = MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(
                                method,
                                fieldPath.ParametersSources[index].Instructions.Last(),
                                jumpTargets);
                            foreach (var path in paths) {
                                workPaths.Add(path);
                            }
                        }
                    }

                    foreach (var loadPath in workPaths) {
                        if (visited.Add(loadPath)) {

                            var loadBegin = loadPath.Instructions.First();
                            var loadEnd = loadPath.Instructions.Last();

                            bool shouldLdObj = false;

                            switch (loadBegin.OpCode.Code) {
                                case Code.Ldnull:
                                    loadBegin.OpCode = OpCodes.Ldsfld;
                                    loadBegin.Operand = tileTypeDef.Fields.Single(f => f.Name == "NULL");
                                    break;
                                case Code.Call:
                                case Code.Callvirt:
                                    var calleeRef = (MethodReference)loadBegin.Operand!;
                                    if (calleeRef.ReturnType is ByReferenceType) {

                                        if (calleeRef.DeclaringType.FullName == tileTypeDef.FullName && calleeRef.Name == "get_" + "NULLREF") {
                                            loadBegin.OpCode = OpCodes.Ldsfld;
                                            loadBegin.Operand = tileTypeDef.Fields.Single(f => f.Name == "NULL");
                                        }
                                        else {
                                            shouldLdObj = true;
                                        }
                                    }
                                    break;
                                case Code.Ldarg:
                                case Code.Ldarg_S:
                                case Code.Ldarg_0:
                                case Code.Ldarg_1:
                                case Code.Ldarg_2:
                                case Code.Ldarg_3:
                                    if (MonoModCommon.IL.TryGetReferencedParameter(method, loadBegin, out var p)) {
                                        if (p.ParameterType is ByReferenceType) {
                                            shouldLdObj = true;
                                        }
                                    }
                                    break;

                                case Code.Ldloc:
                                case Code.Ldloc_S:
                                case Code.Ldloc_0:
                                case Code.Ldloc_1:
                                case Code.Ldloc_2:
                                case Code.Ldloc_3:
                                    if (MonoModCommon.IL.TryGetReferencedVariable(method, loadBegin, out var v)) {
                                        if (v.VariableType is ByReferenceType) {
                                            shouldLdObj = true;
                                        }
                                    }
                                    break;
                            }

                            if (shouldLdObj) {

                                if (loadEnd.OpCode.OperandType is OperandType.ShortInlineBrTarget or OperandType.InlineSwitch) {
                                    var loadObj = Instruction.Create(OpCodes.Ldobj, tileTypeDef);

                                    List<Instruction> inserts = [];

                                    inserts.Add(Instruction.Create(OpCodes.Br, loadEnd.Next));
                                    inserts.Add(loadObj);

                                    if (loadEnd.Operand is Instruction target) {
                                        inserts.Add(Instruction.Create(OpCodes.Br, target));
                                        loadEnd.Operand = loadObj;
                                    }
                                    else if (loadEnd.Operand is Instruction[] targets) {
                                        inserts.Add(Instruction.Create(OpCodes.Switch, targets));
                                        for (int i = 0; i < targets.Length; i++) {
                                            if (targets[i] == instruction) {
                                                targets[i] = loadObj;
                                            }
                                        }
                                    }
                                    iLProcessor.InsertAfter(loadEnd, inserts);
                                }
                                else {
                                    iLProcessor.InsertAfter(loadEnd, Instruction.Create(OpCodes.Ldobj, tileTypeDef));
                                }
                            }
                        }
                    }
                }
            }

            void EachMethod_Adjust_LockObj() {
                foreach (var instructionEnterCheck in method.Body.Instructions.ToArray()) {
                    if (instructionEnterCheck.OpCode != OpCodes.Call) {
                        continue;
                    }
                    var calleeRef_Monitor_Enter = (MethodReference)instructionEnterCheck.Operand!;
                    if (calleeRef_Monitor_Enter.DeclaringType.FullName != typeof(System.Threading.Monitor).FullName ||
                        calleeRef_Monitor_Enter.Name != "Enter") {
                        continue;
                    }
                    var paramPath_Monitor_Enter = MonoModCommon.Stack.AnalyzeParametersSources(method, instructionEnterCheck, jumpTargets);
                    if (paramPath_Monitor_Enter.Length != 1) {
                        throw new NotSupportedException();
                    }

                    var loadObjInstruction_Enter = paramPath_Monitor_Enter[0].ParametersSources[0].Instructions.First();
                    var loadRefFlagInstruction_Enter = paramPath_Monitor_Enter[0].ParametersSources[1].Instructions.First();

                    if (!MonoModCommon.IL.TryGetReferencedVariable(method, loadObjInstruction_Enter, out var variable_tile)) {
                        throw new NotSupportedException();
                    }

                    if (!IsTileType(variable_tile.VariableType)) {
                        continue;
                    }

                    if (!MonoModCommon.IL.TryGetReferencedVariable(method, loadRefFlagInstruction_Enter, out var variable_refFlag)) {
                        throw new NotSupportedException();
                    }

                    var variable_lock = new VariableDefinition(modder.Module.TypeSystem.Object);
                    method.Body.Variables.Add(variable_lock);

                    // insert lock stloc
                    foreach (var instructionEnterSet in method.Body.Instructions.ToArray()) {
                        switch (instructionEnterSet.OpCode.Code) {
                            case Code.Stloc_0:
                            case Code.Stloc_1:
                            case Code.Stloc_2:
                            case Code.Stloc_3:
                            case Code.Stloc_S:
                            case Code.Stloc:
                                break;
                            default:
                                continue;
                        }
                        if (!MonoModCommon.IL.TryGetReferencedVariable(method, instructionEnterSet, out var setVariable)) {
                            continue;
                        }
                        if (setVariable != variable_tile) {
                            continue;
                        }
                        iLProcessor.InsertAfter(instructionEnterSet, [
                            MonoModCommon.IL.BuildVariableLoadAddress(method, method.Body, variable_tile),
                            Instruction.Create(OpCodes.Call, tileTypeDef.GetMethod("GetLock")),
                            MonoModCommon.IL.BuildVariableStore(method, method.Body, variable_lock),
                        ]);
                    }

                    var loadLock = MonoModCommon.IL.BuildVariableLoad(method, method.Body, variable_lock);

                    foreach (var instructionLoadObj in method.Body.Instructions.ToArray()) {
                        switch (instructionLoadObj.OpCode.Code) {
                            case Code.Ldloc_0:
                            case Code.Ldloc_1:
                            case Code.Ldloc_2:
                            case Code.Ldloc_3:
                            case Code.Ldloc_S:
                            case Code.Ldloc:
                                break;
                            default:
                                continue;
                        }
                        if (!MonoModCommon.IL.TryGetReferencedVariable(method, instructionLoadObj, out var loadVariable)) {
                            continue;
                        }
                        if (loadVariable != variable_tile) {
                            continue;
                        }
                        instructionLoadObj.OpCode = loadLock.OpCode;
                        instructionLoadObj.Operand = loadLock.Operand;
                    }
                }
            }

            EachMethod_Adjust_StoreValueToAddress();
            EachMethod_Adjust_VariableDefinitionType();
            EachMethod_Adjust_NullCheck();
            EachMethod_Adjust_LoadAddress();
            EachMethod_Adjust_LoadObjValue();
            EachMethod_Adjust_VariableDefinitionType();
            EachMethod_Adjust_LockObj();
        }
    }

    private void EachMethod_Adjust_MakeRefModifiedLocals(MethodDefinition method,
        Dictionary<Instruction, List<Instruction>> jumpTargets,
        HashSet<VariableDefinition> notReadonlyVariables) {

        bool anyVariableEdit = false;
        if (notReadonlyVariables.Count > 0) {
            do {
                anyVariableEdit = false;
                foreach (var instruction in method.Body.Instructions.ToArray()) {
                    switch (instruction.OpCode.Code) {
                        case Code.Stloc:
                        case Code.Stloc_S:
                        case Code.Stloc_0:
                        case Code.Stloc_1:
                        case Code.Stloc_2:
                        case Code.Stloc_3:
                            var varRef = MonoModCommon.IL.GetReferencedVariable(method, instruction);
                            if (varRef.VariableType is ByReferenceType || !IsTileType(varRef.VariableType)) {
                                continue;
                            }

                            var paths = MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, jumpTargets)
                                .SelectMany(p => MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(method, p.ParametersSources[0].Instructions.Last(), jumpTargets));

                            if (paths.Any(p => p.StackTopType is ByReferenceType)) {

                                Console.WriteLine($"    Modified variable v{varRef.Index} to TileData&");

                                var varDef = varRef.Resolve();

                                if (notReadonlyVariables.Contains(varDef)) {
                                    varDef.VariableType = tileTypeDef.MakeByReferenceType();
                                    anyVariableEdit = true;
                                }
                            }
                            break;
                    }
                }
            }
            while (anyVariableEdit);
        }
    }

    private void EachMethod_Analyze_WillModifyLocals(MethodDefinition method,
        Dictionary<string, HashSet<int>> modifiedTileParameters,
        Dictionary<Instruction, List<Instruction>> jumpTargets,
        out HashSet<VariableDefinition> notReadonlyVariables) {

        notReadonlyVariables = [];

        foreach (var instruction in method.Body.Instructions.ToArray()) {
            if (instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference tileField && IsTileType(tileField.DeclaringType)) {
                var argPaths = MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, jumpTargets);
                foreach (var argPath in argPaths) {
                    var paths = MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(method, argPath.ParametersSources[0].Instructions.Last(), jumpTargets);
                    foreach (var path in paths) {
                        if (MonoModCommon.IL.TryGetReferencedVariable(method, path.Instructions.First(), out var variable)) {
                            notReadonlyVariables.Add(variable);
                        }
                    }
                }
            }
            else if ((instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) && instruction.Operand is MethodReference tileMethod &&
                modifiedTileParameters.TryGetValue(tileMethod.GetIdentifier(), out var theseParametersWillBeEdit)) {

                var paramPaths = MonoModCommon.Stack.AnalyzeParametersSources(method, instruction, jumpTargets);
                foreach (var paramPath in paramPaths) {
                    for (int i = 0; i < paramPath.ParametersSources.Length; i++) {
                        if (!theseParametersWillBeEdit.Contains(i)) {
                            continue;
                        }

                        var paths = MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(method, paramPath.ParametersSources[i].Instructions.Last(), jumpTargets);
                        foreach (var path in paths) {
                            if (MonoModCommon.IL.TryGetReferencedVariable(method, path.Instructions.First(), out var variable)) {
                                notReadonlyVariables.Add(variable);
                            }
                        }
                    }
                }
            }
        }
    }

    private void Adjust_RelinkModifiedComponets(ModuleContext modder,
        Dictionary<string, HashSet<int>> modifiedTileParameters,
        Dictionary<string, MethodDefinition> allMethods,
        Dictionary<string, FieldDefinition> fieldShouldAdjust,
        Dictionary<string, MethodDefinition> methodShouldAdjust,
        out Dictionary<string, Dictionary<string, MethodDefinition>> fieldReferences,
        out Dictionary<string, Dictionary<string, MethodDefinition>> methodsReferences) {

        fieldReferences = [];
        methodsReferences = [];

        string oldTileFullName = tileTypeOldDef.FullName;
        // relink method calls and thisfieldReference
        foreach (var type in modder.Module.GetAllTypes()) {
            foreach (var method in type.Methods) {
                if (!method.HasBody) {
                    continue;
                }

                foreach (var instruction in method.Body.Instructions.ToArray()) {
                    if (instruction.Operand is MethodReference calleeRef) {

                        if (calleeRef.DeclaringType.FullName == oldTileFullName ||
                            calleeRef.ReturnType.FullName == oldTileFullName ||
                            calleeRef.Parameters.Any(
                                p => p.ParameterType.FullName == oldTileFullName ||
                                (p.ParameterType is ByReferenceType referenceType && referenceType.ElementType.FullName == oldTileFullName))) {

                            var declaringType = calleeRef.DeclaringType;
                            if (calleeRef.DeclaringType.FullName == oldTileFullName) {
                                declaringType = tileTypeDef;
                            }

                            HashSet<int> shouldBeReferenceExculdeThis = [];

                            if (modifiedTileParameters.TryGetValue(calleeRef.GetIdentifier(), out var byRefParamIndexesInculdeThis)) {
                                foreach (int indexInculdeThis in byRefParamIndexesInculdeThis) {
                                    int paramIndex = indexInculdeThis;
                                    if (calleeRef.HasThis && instruction.OpCode != OpCodes.Newobj) {
                                        paramIndex -= 1;
                                    }
                                    if (paramIndex != -1) {
                                        shouldBeReferenceExculdeThis.Add(paramIndex);
                                    }
                                }
                            }

                            if (allMethods.TryGetValue(calleeRef.GetIdentifier(true, tileNameMap, shouldBeReferenceExculdeThis), out var methodDefinition)) {
                                instruction.Operand = MonoModCommon.Structure.DeepMapMethodReference(methodDefinition, new());
                            }
                            else {
                                Console.WriteLine($"[Waring] could not find method {calleeRef.GetIdentifier(true, tileNameMap, shouldBeReferenceExculdeThis)} in {declaringType.FullName}");
                            }
                        }
                        else if (calleeRef.Parameters.Any(p => p.ParameterType.FullName == tileCollectionDefOld.FullName) ||
                            calleeRef.ReturnType.FullName == tileCollectionDefOld.FullName) {

                            var declaringType = calleeRef.DeclaringType;

                            if (allMethods.TryGetValue(calleeRef.GetIdentifier(true, tileNameMap), out var methodDefinition)) {
                                instruction.Operand = MonoModCommon.Structure.DeepMapMethodReference(methodDefinition, new());
                            }
                        }
                    }
                    else if (instruction.Operand is FieldReference field) {
                        if (fieldShouldAdjust.TryGetValue(field.GetIdentifier(), out var fieldDefinition)) {
                            instruction.Operand = fieldDefinition;
                        }
                    }
                }
            }
        }


        foreach (var type in modder.Module.GetAllTypes()) {
            foreach (var method in type.Methods) {

                if (!method.HasBody) {
                    continue;
                }

                var jumpTargets = MonoModCommon.Stack.BuildJumpSitesMap(method);

                if (method.Parameters.Any(x => IsTileType(x.ParameterType))) {
                    methodShouldAdjust.TryAdd(method.GetIdentifier(), method);
                }
                if (method.Body.Variables.Any(x => IsTileType(x.VariableType))) {
                    methodShouldAdjust.TryAdd(method.GetIdentifier(), method);
                }

                var iLProcessor = method.Body.GetILProcessor();

                foreach (var instruction in method.Body.Instructions.ToArray()) {

                    if (!methodShouldAdjust.ContainsKey(method.GetIdentifier())) {
                        if (instruction.Operand is IMemberDefinition member && member.DeclaringType is not null && IsTileType(member.DeclaringType)) {
                            methodShouldAdjust.TryAdd(method.GetIdentifier(), method);
                        }
                        else if (instruction.Operand is MethodReference methodRef) {
                            if (IsTileType(methodRef.DeclaringType) || methodRef.Parameters.Any(x => IsTileType(x.ParameterType))) {
                                methodShouldAdjust.TryAdd(method.GetIdentifier(), method);
                            }
                        }
                    }

                    if (instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Newobj) {
                        var callee = (MethodReference)instruction.Operand!;
                        string calleeId = callee.GetIdentifier();
                        if (!methodsReferences.TryGetValue(calleeId, out var whoCalls)) {
                            methodsReferences[calleeId] = whoCalls = [];
                        }
                        whoCalls.TryAdd(method.GetIdentifier(), method);

                        if (callee.Parameters.Any(x => IsTileType(x.ParameterType))) {

                            var paths = MonoModCommon.Stack.AnalyzeParametersSources(method, instruction, jumpTargets);

                            foreach (var path in paths) {
                                var parameterPreparations = path.ParametersSources;
                                for (int i = 0; i < parameterPreparations.Length; i++) {
                                    if (i == 0 && instruction.OpCode == OpCodes.Newobj) {
                                        continue;
                                    }
                                    var preparation = parameterPreparations[i];
                                    if (IsTileType(preparation.Parameter.ParameterType)) {
                                        if (preparation.Instructions.Length == 1) {
                                            var preparationIL = preparation.Instructions[0];
                                            if (preparationIL.OpCode == OpCodes.Ldnull) {
                                                preparationIL.OpCode = OpCodes.Ldsfld;
                                                preparationIL.Operand = tileTypeDef.Fields.Single(f => f.Name == "NULL");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (instruction.Operand is FieldReference tileField) {
                        string tileFieldId = tileField.GetIdentifier();
                        if (!fieldReferences.TryGetValue(tileFieldId, out var thisfieldReference)) {
                            fieldReferences[tileFieldId] = thisfieldReference = [];
                        }
                        thisfieldReference.TryAdd(method.GetIdentifier(), method);

                        if (tileField.DeclaringType.FullName == "Terraria.Main" && tileField.Name == "tile") {
                            if (instruction.OpCode == OpCodes.Stsfld) {
                                // vanilla: Main.tile = new Tile[width, height]  ->  TileCollection.Create(width, height, callingMethod)
                                var prev = instruction.Previous;
                                prev.OpCode = OpCodes.Call;
                                prev.Operand = tileCollection_CreateMDef;
                                method.Body.GetILProcessor().InsertBefore(prev, Instruction.Create(OpCodes.Ldstr, "Main..cctor"));
                            }
                            instruction.Operand = tileCollectionFieldDefInMain;
                        }
                    }
                    else if (instruction.OpCode == OpCodes.Newobj) {
                        var calleeRef = (MethodReference)instruction.Operand!;
                        if (calleeRef.DeclaringType.FullName == "Terraria.Tile") {
                            if (calleeRef.Parameters.Count == 0) {
                                instruction.OpCode = OpCodes.Call;
                                instruction.Operand = tileCreate;
                            }
                            else if (calleeRef.Parameters.Count == 1) {
                                instruction.OpCode = OpCodes.Call;
                                instruction.Operand = tileCreateWithExistingTile;
                            }
                        }
                    }
                    else if (instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Call) {
                        var calleeRef = (MethodReference)instruction.Operand!;

                        if (calleeRef.DeclaringType.FullName == tileCollectionDefOld.FullName || calleeRef.DeclaringType.FullName == tileCollectionDef.FullName) {
                            if (calleeRef.Name == "get_Item") {
                                methodShouldAdjust.TryAdd(method.GetIdentifier(), method);
                            }
                            else if (calleeRef.Name == "set_Item") {

                                methodShouldAdjust.TryAdd(method.GetIdentifier(), method);

                                var local = new VariableDefinition(tileTypeDef);
                                method.Body.Variables.Add(local);

                                var setLocal = MonoModCommon.IL.BuildVariableStore(method, method.Body, local);

                                instruction.OpCode = setLocal.OpCode;
                                instruction.Operand = setLocal.Operand;

                                iLProcessor.InsertAfter(instruction, [
                                    Instruction.Create(OpCodes.Callvirt, tileCollection_getItemMDef),
                                    Instruction.Create(OpCodes.Ldloc, local),
                                    Instruction.Create(OpCodes.Stobj, tileTypeDef),
                                ]);
                            }
                        }
                        else if (calleeRef.DeclaringType.FullName == oldTileFullName) {

                            if (calleeRef.Name.OrdinalStartsWith("get_")) {
                                string fieldName = calleeRef.Name.Substring(4);
                                instruction.OpCode = OpCodes.Ldfld;
                                instruction.Operand = tileTypeDef.FindField(fieldName) ?? throw new NotSupportedException($"Field {fieldName} not found");
                            }
                            else if (calleeRef.Name.OrdinalStartsWith("set_")) {
                                string fieldName = calleeRef.Name.Substring(4);
                                instruction.OpCode = OpCodes.Stfld;
                                instruction.Operand = tileTypeDef.FindField(fieldName) ?? throw new NotSupportedException($"Field {fieldName} not found");
                            }
                            else {
                                instruction.OpCode = OpCodes.Call;
                                MethodReference? newerMethod = null;
                                foreach (var m in tileTypeDef.Methods) {
                                    if (m.Name != calleeRef.Name) {
                                        continue;
                                    }
                                    if (m.Parameters.Count != calleeRef.Parameters.Count) {
                                        continue;
                                    }
                                    for (int i = 0; i < m.Parameters.Count; i++) {
                                        if (m.Parameters[i].ParameterType.FullName != calleeRef.Parameters[i].ParameterType.FullName) {
                                            continue;
                                        }
                                    }
                                    newerMethod = m;
                                    break;
                                }
                                instruction.Operand = newerMethod ?? throw new NotSupportedException();
                            }
                        }
                    }
                }
            }
        }
    }

    private void Analyze_ComponentsNeedAdjust(ModuleContext modder,
        Dictionary<string, HashSet<int>> modifiedTileParameters,
        out Dictionary<string, FieldDefinition> fieldShouldAdjust,
        out Dictionary<string, MethodDefinition> methodShouldAdjust) {
        fieldShouldAdjust = [];
        methodShouldAdjust = [];
        string oldTileFullName = tileTypeOldDef.FullName;

        foreach (var type in modder.Module.GetAllTypes()) {
            foreach (var field in type.Fields) {
                var fieldType = field.FieldType;
                if (fieldType.FullName == oldTileFullName) {
                    field.FieldType = tileTypeDef;
                    fieldShouldAdjust.TryAdd(field.GetIdentifier(), field);
                }
                if (fieldType.FullName == tileCollectionDefOld.FullName) {
                    field.FieldType = tileCollectionDef;
                    fieldShouldAdjust.TryAdd(field.GetIdentifier(), field);
                }
                if (fieldType.HasGenericParameters) {
                    bool anyEdit = false;
                    foreach (var parameter in fieldType.GenericParameters) {
                        foreach (var c in parameter.Constraints) {
                            if (c.ConstraintType.FullName == oldTileFullName) {
                                c.ConstraintType = tileTypeDef;
                                anyEdit = true;
                            }
                        }
                    }
                    if (anyEdit) {
                        fieldShouldAdjust.TryAdd(field.GetIdentifier(), field);
                    }
                }
            }
            foreach (var prop in type.Properties) {
                if (prop.PropertyType.FullName == oldTileFullName) {
                    prop.PropertyType = tileTypeDef;
                }
                if (prop.PropertyType.FullName == tileCollectionDefOld.FullName) {
                    prop.PropertyType = tileCollectionDef;
                }
            }
            foreach (var method in type.Methods) {

                bool canAdd = false;

                if (method.ReturnType.FullName == oldTileFullName) {
                    method.ReturnType = tileTypeDef;
                    canAdd = true;
                }
                if (method.ReturnType.FullName == tileCollectionDefOld.FullName) {
                    method.ReturnType = tileCollectionDef;
                    canAdd = true;
                }

                if (method.HasGenericParameters) {
                    foreach (var parameter in method.GenericParameters) {
                        foreach (var c in parameter.Constraints) {
                            if (c.ConstraintType.FullName == oldTileFullName) {
                                c.ConstraintType = tileTypeDef;
                                canAdd = true;
                            }
                            if (c.ConstraintType.FullName == tileCollectionDefOld.FullName) {
                                c.ConstraintType = tileCollectionDef;
                                canAdd = true;
                            }
                        }
                    }
                }

                if (!modifiedTileParameters.TryGetValue(method.GetIdentifier(), out var byRefParamIndexesInculdeThis)) {
                    byRefParamIndexesInculdeThis = [];
                }

                foreach (var parameter in method.Parameters) {
                    bool paramIsTileRef = parameter.ParameterType is ByReferenceType referenceType && IsTileType(referenceType.ElementType);

                    if (parameter.ParameterType.FullName == oldTileFullName || paramIsTileRef) {
                        int index = method.Parameters.IndexOf(parameter);
                        int indexInculdeThis = index;
                        if (method.HasThis) {
                            indexInculdeThis += 1;
                        }
                        if (paramIsTileRef || byRefParamIndexesInculdeThis.Contains(indexInculdeThis)) {
                            method.Parameters[index].ParameterType = tileTypeDef.MakeByReferenceType();
                        }
                        else {
                            method.Parameters[index].ParameterType = tileTypeDef;
                        }
                        canAdd = true;
                    }
                    if (parameter.ParameterType.FullName == tileCollectionDefOld.FullName) {
                        parameter.ParameterType = tileCollectionDef;
                        canAdd = true;
                    }
                }

                if (method.HasBody) {
                    foreach (var variable in method.Body.Variables) {
                        if (variable.VariableType.FullName == oldTileFullName) {
                            variable.VariableType = tileTypeDef;
                            canAdd = true;
                        }
                    }
                    foreach (var variable in method.Body.Variables) {
                        if (variable.VariableType.FullName == tileCollectionDefOld.FullName) {
                            variable.VariableType = tileCollectionDefOld;
                            canAdd = true;
                        }
                    }

                    foreach (var instruction in method.Body.Instructions) {
                        switch (instruction.OpCode.Code) {
                            case Code.Call:
                            case Code.Callvirt:
                            case Code.Newobj:
                                var methodRef = (MethodReference)instruction.Operand;
                                if (IsTileType(methodRef.ReturnType)) {
                                    canAdd = true;
                                    break;
                                }
                                if (methodRef.Parameters.Any(x => IsTileType(x.ParameterType))) {
                                    canAdd = true;
                                    break;
                                }
                                break;
                            case Code.Ldsfld:
                            case Code.Ldsflda:
                            case Code.Ldfld:
                            case Code.Ldflda:
                                var fieldRef = (FieldReference)instruction.Operand;
                                if (IsTileType(fieldRef.FieldType)) {
                                    canAdd = true;
                                    break;
                                }
                                break;
                        }
                        if (canAdd) {
                            break;
                        }
                    }
                }

                if (canAdd) {
                    methodShouldAdjust.TryAdd(method.GetIdentifier(), method);
                }
            }
        }
    }

    private void Adjust_MethodReturnTileRef(ModuleContext modder) {
        int lastCount;
        Dictionary<string, MethodDefinition> retRefTileMethods = [];
        do {
            lastCount = retRefTileMethods.Count;

            foreach (var type in modder.Module.GetAllTypes()) {
                foreach (var method in type.Methods) {

                    if (!method.HasBody || method.ReturnType is ByReferenceType || !IsTileType(method.ReturnType)) {
                        continue;
                    }

                    var cachedJumpTargets = MonoModCommon.Stack.BuildJumpSitesMap(method);

                    var retInstructions = method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToArray();

                    Queue<MonoModCommon.Stack.StackTopTypePath> works = new();
                    HashSet<MonoModCommon.Stack.StackTopTypePath> visited = [];
                    var paths = retInstructions
                        .SelectMany(ret => MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, ret, cachedJumpTargets))
                        .SelectMany(path => MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(method, path.ParametersSources[0].Instructions.Last(), cachedJumpTargets))
                        .ToHashSet();
                    foreach (var path in paths) {
                        works.Enqueue(path);
                    }
                    bool anyRefReturn = false;

                    while (!(works.Count == 0 || anyRefReturn)) {
                        var path = works.Dequeue();
                        visited.Add(path);

                        var topInstruction = path.Instructions.First();
                        switch (topInstruction.OpCode.Code) {
                            case Code.Call:
                            case Code.Callvirt: {
                                    var calleeRef = (MethodReference)topInstruction.Operand!;
                                    if (calleeRef.ReturnType is ByReferenceType referenceType && IsTileType(referenceType.ElementType)) {
                                        anyRefReturn = true;
                                    }
                                    if (retRefTileMethods.TryGetValue(calleeRef.GetIdentifier(), out var retRefTileMethod)) {
                                        topInstruction.Operand = MonoModCommon.Structure.DeepMapMethodReference(retRefTileMethod, new());
                                        anyRefReturn = true;
                                    }
                                    break;
                                }
                            case Code.Ldarg_0:
                            case Code.Ldarg_1:
                            case Code.Ldarg_2:
                            case Code.Ldarg_3:
                            case Code.Ldarg_S:
                            case Code.Ldarg: {
                                    var parameter = MonoModCommon.IL.GetReferencedParameter(method, topInstruction);
                                    if (parameter.ParameterType is ByReferenceType referenceType && IsTileType(referenceType.ElementType)) {
                                        anyRefReturn = true;
                                    }
                                }
                                break;
                            case Code.Ldloc_0:
                            case Code.Ldloc_1:
                            case Code.Ldloc_2:
                            case Code.Ldloc_3:
                            case Code.Ldloc_S:
                            case Code.Ldloc:
                                var variable = MonoModCommon.IL.GetReferencedVariable(method, topInstruction);

                                foreach (var instruction in method.Body.Instructions.ToArray()) {
                                    if (instruction.OpCode.Code is
                                        Code.Stloc_0 or
                                        Code.Stloc_1 or
                                        Code.Stloc_2 or
                                        Code.Stloc_3 or
                                        Code.Stloc_S or
                                        Code.Stloc) {

                                        var checkVariable = MonoModCommon.IL.GetReferencedVariable(method, instruction);

                                        if (checkVariable == variable) {
                                            foreach (var sourcePath in MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, cachedJumpTargets)) {
                                                foreach (var variablePath in MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(method, sourcePath.ParametersSources[0].Instructions.Last())) {
                                                    if (!visited.Contains(variablePath)) {
                                                        works.Enqueue(variablePath);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                break;
                        }
                    }

                    if (anyRefReturn) {
                        method.ReturnType = tileTypeDef.MakeByReferenceType();

                        retRefTileMethods.Add(method.GetIdentifier(), method);

                        foreach (var path in paths) {
                            if (path.StackTopType is ByReferenceType) {
                                continue;
                            }

                            var topInstruction = path.Instructions.First();
                            switch (topInstruction.OpCode.Code) {
                                case Code.Ldnull:
                                    topInstruction.OpCode = OpCodes.Call;
                                    topInstruction.Operand = tileTypeDef.GetMethod("get_" + "NULLREF");
                                    break;
                                case Code.Ldarg_0:
                                case Code.Ldarg_1:
                                case Code.Ldarg_2:
                                case Code.Ldarg_3:
                                case Code.Ldarg_S:
                                case Code.Ldarg:
                                    var parameter = MonoModCommon.IL.GetReferencedParameter(method, topInstruction);
                                    if (!IsTileType(parameter.ParameterType)) {
                                        break;
                                    }
                                    parameter.ParameterType = tileTypeDef.MakeByReferenceType();
                                    break;
                                case Code.Ldloc_0:
                                case Code.Ldloc_1:
                                case Code.Ldloc_2:
                                case Code.Ldloc_3:
                                case Code.Ldloc_S:
                                case Code.Ldloc:
                                    var variable = MonoModCommon.IL.GetReferencedVariable(method, topInstruction);
                                    variable.VariableType = tileTypeDef.MakeByReferenceType();
                                    break;
                                case Code.Ldfld:
                                    topInstruction.OpCode = OpCodes.Ldflda;
                                    break;
                                case Code.Ldsfld:
                                    topInstruction.OpCode = OpCodes.Ldsflda;
                                    break;
                                case Code.Call:
                                case Code.Callvirt:
                                    var calleeRef = (MethodReference)topInstruction.Operand!;
                                    if (calleeRef.Name == "InvokeCreate" && calleeRef.DeclaringType.FullName == "OTAPI.Hooks/Tile") {
                                        topInstruction.OpCode = OpCodes.Call;
                                        if (calleeRef.Parameters.Count == 0) {
                                            topInstruction.OpCode = OpCodes.Call;
                                            topInstruction.Operand = tileTypeDef.GetMethod("get_" + "EMPTYREF");
                                        }
                                        else {
                                            topInstruction.OpCode = OpCodes.Call;
                                            topInstruction.Operand = tileTypeDef.Methods.First(m =>
                                            m.Parameters.Count == calleeRef.Parameters.Count &&
                                            m.Name == "GetEMPTYREF");
                                        }
                                    }
                                    break;
                            }
                        }
                    }
                }
            }
        }
        while (lastCount != retRefTileMethods.Count);

        Console.WriteLine($"Found {retRefTileMethods.Count} methods that return TileData&");
    }

    private void Adjust_MFWHMethods(ModuleContext modder, Dictionary<string, HashSet<int>> modifiedTileParameters) {
        foreach (var type in modder.Module.GetAllTypes()) {
            foreach (var method in type.Methods) {
                if (!method.Parameters.Any(p => IsTileType(p.ParameterType))) {
                    continue;
                }
                if (!method.HasBody) {
                    continue;
                }

                var jumpTargets = MonoModCommon.Stack.BuildJumpSitesMap(method);

                MethodReference? mfwhMethod = null;
                Instruction? mfwhCallInstruction = null;
                MethodReference? invokeMethod = null;
                Instruction? invokeCallInstruction = null;
                Instruction? firstSetLocal = null;

                foreach (var instruction in method.Body.Instructions) {
                    if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) {
                        var calleeRef = (MethodReference)instruction.Operand!;

                        if (calleeRef.Name.OrdinalStartsWith("mfwh_")) {
                            mfwhMethod = calleeRef;
                            mfwhCallInstruction = instruction;
                        }
                    }
                }

                if (mfwhMethod is null) {
                    continue;
                }

                // Adjust parameters
                foreach (var instruction in method.Body.Instructions) {
                    if (instruction.OpCode != OpCodes.Newobj) {
                        continue;
                    }
                    var delegateDef = ((MethodReference)instruction.Operand).DeclaringType.Resolve();
                    if (!delegateDef.IsDelegate()) {
                        continue;
                    }
                    var beginInvoke = delegateDef.GetMethod("BeginInvoke");
                    var invoke = delegateDef.GetMethod("Invoke");
                    for (int i = 0; i < method.Parameters.Count; i++) {
                        beginInvoke.Parameters[i] = method.Parameters[i].Clone();
                        invoke.Parameters[i] = method.Parameters[i].Clone();
                    }
                }

                foreach (var instruction in method.Body.Instructions) {
                    if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) {
                        var calleeRef = (MethodReference)instruction.Operand!;

                        if (calleeRef.Name == mfwhMethod.Name.Replace("mfwh_", "Invoke")) {
                            invokeMethod = calleeRef;
                            invokeCallInstruction = instruction;
                        }
                    }
                }

                foreach (var instruction in method.Body.Instructions.Reverse()) {
                    if (instruction.OpCode == OpCodes.Stloc) {
                        firstSetLocal = instruction;
                        break;
                    }
                }

                if (modifiedTileParameters.TryGetValue(mfwhMethod.GetIdentifier(), out var indexes)) {
                    modifiedTileParameters.TryAdd(method.GetIdentifier(), indexes);

                    var invokeCallPaths = MonoModCommon.Stack.AnalyzeParametersSources(method, invokeCallInstruction!, jumpTargets);
                    if (invokeCallPaths.Length > 1) {
                        throw new Exception($"Unexpected IL structure: multiple calls to EventHooks.Invoke{method.Name} in the method");
                    }
                    var mfwhCallPaths = MonoModCommon.Stack.AnalyzeParametersSources(method, mfwhCallInstruction!, jumpTargets);
                    if (mfwhCallPaths.Length > 1) {
                        throw new Exception($"Unexpected IL structure: multiple calls to the original implementation mfwh_{method.Name} in the method");
                    }

                    var invokeCallPath = invokeCallPaths[0];
                    var mfwhCallPath = mfwhCallPaths[0];

                    List<Instruction> refParameterAssignments = [];

                    foreach (int index in indexes) {
                        int paramIndexExculdeThis = index;
                        if (method.HasThis) {
                            paramIndexExculdeThis--;
                        }

                        // Exclude the 'this' Parameter to make index only indicate the parameters in method.Parameters
                        if (paramIndexExculdeThis == -1) {
                            continue;
                        }

                        // The InvokeXXX is a static function, the required parameters for the call are as follows:
                        // 0. The 'this' object of the tail function, or null if the tail function is static
                        // 1. The original method logic delegate of the tail function, which is mfwh_XXX
                        // 2 and more... The parameters of the tail function
                        // Therefore, the mapping relationship from mfwh_XXX input Parameter index to InvokeXXX input Parameter index is +2
                        paramIndexExculdeThis += 2;

                        var loadParamInsts = invokeCallPath.ParametersSources[paramIndexExculdeThis].Instructions;
                        if (loadParamInsts.Length is 1) {
                            var loadParam = loadParamInsts.Single();

                            if (!MonoModCommon.IL.TryGetReferencedParameter(method, loadParam, out var parameter)) {
                                throw new NotSupportedException($"Cannot get the {index + 1}th TracingParameter of Invoke{method.Name}");
                            }

                            var iLProcessor = method.Body.GetILProcessor();

                            // Since we expect to modify the parameters of the tail method to match the ref parameters of mfwh_XXX,
                            // we need to extract the value of the ref Parameter of the tail method when calling the InvokeXXX input parameters
                            iLProcessor.InsertAfter(loadParam, Instruction.Create(OpCodes.Ldobj, tileTypeDef));

                            // Next, we need to extract the mapping relationship between the parameters and event fields from the input parameters of mfwh_XXX,
                            // so we need to convert paramIndex from InvokeXXX to the index of the input parameters of mfwh_XXX
                            paramIndexExculdeThis -= 2;

                            var loadField = mfwhCallPath.ParametersSources[paramIndexExculdeThis].Instructions.Last();

                            if (loadField.OpCode != OpCodes.Ldfld && loadField.OpCode != OpCodes.Ldflda) {
                                throw new NotSupportedException($"The {index + 1}th TracingParameter of mfwh_{method.Name} is not a field");
                            }

                            var field = (FieldReference)loadField.Operand!;

                            loadParam = MonoModCommon.IL.BuildParameterLoad(method, method.Body, method.Parameters[paramIndexExculdeThis]);

                            // Ensure that the ref Parameter can get the updated value after the InvokeXXX call
                            iLProcessor.InsertAfter(firstSetLocal!, [
                                loadParam,
                            Instruction.Create(OpCodes.Ldloc_0),
                            Instruction.Create(OpCodes.Ldfld, field),
                            Instruction.Create(OpCodes.Stobj, tileTypeDef),
                        ]);

                            loadField.Previous.OpCode = OpCodes.Nop;
                            loadField.Previous.Operand = null;
                            loadField.OpCode = loadParam.OpCode;
                            loadField.Operand = loadParam.Operand;
                        }
                        else if (loadParamInsts.Length is 2 && loadParamInsts[1].OpCode.Code is Code.Ldind_Ref or Code.Ldobj) {
                            loadParamInsts[1].OpCode = OpCodes.Ldobj;
                            loadParamInsts[1].Operand = tileTypeDef;
                        }
                        else {
                            throw new NotSupportedException($"Unexpected IL structure when loading the {index + 1}th TracingParameter of Invoke{method.Name}");
                        }
                    }
                }
            }
        }
    }

    private void Analyze_ModifiedTileParameter(ModuleContext modder, out Dictionary<string, HashSet<int>> modifiedTileParameter) {
        Console.WriteLine($"Preparing to analyze non-readonly methods");

        modifiedTileParameter = [];

        // Find non-readonly methods that modify fields

        foreach (var method in tileTypeDef.Methods.Where(m => !m.IsStatic && !m.IsConstructor)) {
            foreach (var instr in method.Body.Instructions.Where(instr => instr.OpCode == OpCodes.Stfld)) {
                var field = (FieldReference)instr.Operand!;
                if (field.DeclaringType.FullName == tileTypeDef.FullName) {
                    foreach (var path in MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instr, MonoModCommon.Stack.BuildJumpSitesMap(method))) {
                        if (MonoModCommon.IL.TryGetReferencedParameter(method, path.ParametersSources[0].Instructions.Last(), out var parameter) && method.Body.ThisParameter == parameter) {
                            modifiedTileParameter.Add(method.GetIdentifier(), [0]);
                            goto nextMethod;
                        }
                    }
                }
            }
        nextMethod:;
        }

        // Find non-readonly methods that call non-readonly methods
        Analyze_NonReadOnlyParameter([tileTypeDef], ref modifiedTileParameter);

        // Copy this non-readonly method from TileData to the original ITile
        foreach (var method in tileTypeOldDef.Methods) {
            if (modifiedTileParameter.TryGetValue(method.GetIdentifier(true, tileNameMap), out var byRefParamIndexesInculdeThis)) {
                modifiedTileParameter.Add(method.GetIdentifier(), byRefParamIndexesInculdeThis);
            }
        }
        foreach (var set_method in tileTypeOldDef.Methods.Where(m => m.Name.OrdinalStartsWith("set_"))) {
            modifiedTileParameter.Add(set_method.GetIdentifier(), [0]);
        }

        Console.WriteLine($"Initially found {modifiedTileParameter.Count} non-readonly methods:");
        foreach (string method in modifiedTileParameter.Keys) {
            Console.WriteLine(" " + method);
        }
        Analyze_NonReadOnlyParameter(modder.Module.GetAllTypes(), ref modifiedTileParameter);
        Console.WriteLine($"Found {modifiedTileParameter.Count} methods that are not readonly");
    }

    private void Replace_TileCollection(ModuleContext modder) {
        foreach (var type in modder.Module.GetAllTypes()) {
            foreach (var method in type.Methods) {
                if (!method.HasBody) {
                    continue;
                }

                foreach (var local in method.Body.Variables) {
                    if (local.VariableType.FullName == tileCollectionDefOld.FullName) {
                        local.VariableType = tileCollectionDef;
                    }
                }

                foreach (var instruction in method.Body.Instructions.ToArray()) {
                    if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) {
                        var calleeRef = (MethodReference)instruction.Operand!;
                        // Raw vanilla (no ITile/collection PreMerge mods) accesses Main.tile[x,y] as
                        // Tile[,]::Get / Tile[,]::Set. Redirect both to the TileCollection indexer.
                        if (calleeRef.Name == "get_Item" || (calleeRef.Name == "Get" && calleeRef.DeclaringType is ArrayType)) {
                            if (calleeRef.DeclaringType.FullName == tileCollectionDefOld.FullName) {
                                instruction.OpCode = OpCodes.Callvirt;
                                instruction.Operand = tileCollection_getItemMDef;
                            }
                        }
                        else if (calleeRef.Name == "Set" && calleeRef.DeclaringType is ArrayType
                            && calleeRef.DeclaringType.FullName == tileCollectionDefOld.FullName) {
                            // Tile[,]::Set(x, y, value) -> TileData& ref = get_Item(x,y); ref = value;
                            var local = new VariableDefinition(tileTypeDef);
                            method.Body.Variables.Add(local);
                            var setLocal = MonoModCommon.IL.BuildVariableStore(method, method.Body, local);
                            instruction.OpCode = setLocal.OpCode;
                            instruction.Operand = setLocal.Operand;
                            method.Body.GetILProcessor().InsertAfter(instruction, [
                                Instruction.Create(OpCodes.Callvirt, tileCollection_getItemMDef),
                                Instruction.Create(OpCodes.Ldloc, local),
                                Instruction.Create(OpCodes.Stobj, tileTypeDef),
                            ]);
                        }
                    }
                }
            }
        }
    }
    readonly Dictionary<string, TypeDefinition> byRefDelegateDefs = [];
    private void Replace_TileDelegate(ModuleContext modder) {
        void Transform(TypeReference type, Action<GenericInstanceType> replace, ModuleDefinition module) {
            if (type is not GenericInstanceType gen) {
                return;
            }
            string etName = gen.ElementType.FullName;
            bool isAction = etName.StartsWith("System.Action`");
            bool isFunc = etName.StartsWith("System.Func`");

            if (isAction || isFunc) {
                int gaCount = gen.GenericArguments.Count;
                int paramCount = isFunc ? gaCount - 1 : gaCount;

                string key = isFunc ? "Func" : "Action";
                bool any = false;

                for (int i = 0; i < paramCount; i++) {
                    if (IsTileType(gen.GenericArguments[i], handleByRef: false, includingOriginal: true)) {
                        key += "ref";
                        any = true;
                    }
                    else {
                        key += "_";
                    }
                }

                if (!any) {
                    return;
                }

                key += $"`{gaCount}";

                if (!byRefDelegateDefs.TryGetValue(key, out var delegateDef)) {
                    #region Create DelegateDef
                    delegateDef = new TypeDefinition(
                        Constants.DelegatesNameSpace,
                        key,
                        TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                        new TypeReference("System", "MulticastDelegate", module, module.TypeSystem.CoreLibrary)
                    );

                    // .ctor(object, IntPtr)
                    var ctor = new MethodDefinition(
                        ".ctor",
                        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName,
                        module.TypeSystem.Void
                    );
                    ctor.Parameters.Add(new ParameterDefinition("object", ParameterAttributes.None, module.TypeSystem.Object));
                    ctor.Parameters.Add(new ParameterDefinition("method", ParameterAttributes.None, module.TypeSystem.IntPtr));
                    ctor.ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed;
                    delegateDef.Methods.Add(ctor);

                    var gps = new GenericParameter[gaCount];
                    for (int i = 0; i < gaCount; i++) {
                        var gp = new GenericParameter(delegateDef);
                        gp.Name = $"T{i + 1}";
                        delegateDef.GenericParameters.Add(gp);
                        gps[i] = gp;
                    }

                    var methodAtt = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual;
                    var runtimeImpl = MethodImplAttributes.Runtime | MethodImplAttributes.Managed;

                    var invokeReturn = isFunc ? gps[gaCount - 1] : module.TypeSystem.Void;

                    var iasyncResultType = new TypeReference("System", "IAsyncResult", module, module.TypeSystem.CoreLibrary);
                    var asyncCallbackType = new TypeReference("System", "AsyncCallback", module, module.TypeSystem.CoreLibrary);

                    var invoke = new MethodDefinition("Invoke", methodAtt, invokeReturn) { ImplAttributes = runtimeImpl };
                    var beginInvoke = new MethodDefinition("BeginInvoke", methodAtt, iasyncResultType) { ImplAttributes = runtimeImpl };
                    var endInvoke = new MethodDefinition("EndInvoke", methodAtt, invokeReturn) { ImplAttributes = runtimeImpl };

                    for (int i = 0; i < paramCount; i++) {
                        var pType = IsTileType(gen.GenericArguments[i], handleByRef: false, includingOriginal: true)
                            ? gps[i].MakeByReferenceType()
                            : (TypeReference)gps[i];

                        invoke.Parameters.Add(new ParameterDefinition($"arg{i + 1}", ParameterAttributes.None, pType));
                        beginInvoke.Parameters.Add(new ParameterDefinition($"arg{i + 1}", ParameterAttributes.None, pType));
                        if (pType is ByReferenceType) {
                            endInvoke.Parameters.Add(new ParameterDefinition($"arg{i + 1}", ParameterAttributes.None, pType));
                        }
                    }

                    // BeginInvoke( ... , AsyncCallback, object)
                    beginInvoke.Parameters.Add(new ParameterDefinition("callback", ParameterAttributes.None, asyncCallbackType));
                    beginInvoke.Parameters.Add(new ParameterDefinition("object", ParameterAttributes.None, module.TypeSystem.Object));

                    // EndInvoke(IAsyncResult)
                    endInvoke.Parameters.Add(new ParameterDefinition("result", ParameterAttributes.None, iasyncResultType));

                    delegateDef.Methods.AddRange([invoke, beginInvoke, endInvoke]);
                    module.Types.Add(delegateDef);
                    byRefDelegateDefs.Add(key, delegateDef);

                    #endregion
                }

                var genericDelegateRef = new GenericInstanceType(delegateDef);
                foreach (var ga in gen.GenericArguments) {
                    if (IsTileType(ga, handleByRef: false, includingOriginal: true)) {
                        genericDelegateRef.GenericArguments.Add(tileTypeDef);
                    }
                    else {
                        genericDelegateRef.GenericArguments.Add(ga);
                    }
                }
                replace(genericDelegateRef);
            }
        }

        HashSet<VariableDefinition> deleLocals = [];
        foreach (var type in modder.Module.GetAllTypes().ToArray()) {
            foreach (var field in type.Fields) {
                Transform(field.FieldType, n => field.FieldType = n, modder.Module);
            }
            foreach (var prop in type.Properties) {
                Transform(prop.PropertyType, n => prop.PropertyType = n, modder.Module);
            }
            foreach (var method in type.Methods) {
                foreach (var p in method.Parameters) {
                    Transform(p.ParameterType, n => p.ParameterType = n, modder.Module);
                }
                Transform(method.ReturnType, n => method.ReturnType = n, modder.Module);

                if (!method.HasBody) {
                    continue;
                }


                foreach (var local in method.Body.Variables) {
                    Transform(local.VariableType, n => local.VariableType = n, modder.Module);
                }

                foreach (var inst in method.Body.Instructions) {
                    switch (inst.Operand) {
                        case FieldReference field:
                            Transform(field.FieldType, n => field.FieldType = n, modder.Module);

                            break;
                        case PropertyReference prop:
                            Transform(prop.PropertyType, n => prop.PropertyType = n, modder.Module);
                            break;
                        case MethodReference mRef:
                            Transform(mRef.DeclaringType, n => {

                                if (inst.OpCode == OpCodes.Newobj) {
                                    Instruction ldftn = inst;
                                    while (ldftn.OpCode != OpCodes.Ldftn) {
                                        ldftn = inst.Previous;
                                    }
                                    var targetMethodRef = (MethodReference)ldftn.Operand;
                                    var targetMethodDef = targetMethodRef.Resolve();
                                    foreach (var p in targetMethodRef.Parameters) {
                                        if (IsTileType(p.ParameterType, handleByRef: false, includingOriginal: true)) {
                                            p.ParameterType = tileTypeDef.MakeByReferenceType();
                                        }
                                    }
                                    foreach (var p in targetMethodDef.Parameters) {
                                        if (IsTileType(p.ParameterType, handleByRef: false, includingOriginal: true)) {
                                            p.ParameterType = tileTypeDef.MakeByReferenceType();
                                        }
                                    }
                                }

                                var old = (GenericInstanceType)mRef.DeclaringType;
                                mRef.DeclaringType = n;
                                if (mRef.Name is "Invoke" or "BeginInvoke") {
                                    if (n.ElementType.FullName.StartsWith("System.Action")) {
                                        for (int i = 0; i < n.GenericArguments.Count; i++) {
                                            var p = mRef.Parameters[i];
                                            p.ParameterType = n.ElementType.GenericParameters[((GenericParameter)p.ParameterType).Position];
                                            if (IsTileType(old.GenericArguments[i], handleByRef: false, includingOriginal: true)) {
                                                p.ParameterType = p.ParameterType.MakeByReferenceType();
                                            }
                                        }
                                    }
                                    else {
                                        for (int i = 0; i < n.GenericArguments.Count - 1; i++) {
                                            var p = mRef.Parameters[i];
                                            p.ParameterType = n.ElementType.GenericParameters[((GenericParameter)p.ParameterType).Position];
                                            if (IsTileType(old.GenericArguments[i], handleByRef: false, includingOriginal: true)) {
                                                p.ParameterType = p.ParameterType.MakeByReferenceType();
                                            }
                                        }
                                    }
                                }

                            }, modder.Module);
                            foreach (var p in mRef.Parameters) {
                                Transform(p.ParameterType, n => p.ParameterType = n, modder.Module);
                            }
                            Transform(mRef.ReturnType, n => mRef.ReturnType = n, modder.Module);
                            break;
                    }
                }
            }
        }
    }

    private void Replace_GenericParamAndArgs(ModuleContext modder) {
        foreach (var type in modder.Module.GetAllTypes()) {
            if (type.HasGenericParameters) {
                foreach (var parameter in type.GenericParameters) {
                    foreach (var c in parameter.Constraints) {
                        if (c.ConstraintType.FullName == tileTypeOldDef.FullName) {
                            c.ConstraintType = tileTypeDef.MakeByReferenceType();
                        }
                    }
                }
            }
        }
    }

    private void Analyze_NonReadOnlyParameter(IEnumerable<TypeDefinition> types, ref Dictionary<string, HashSet<int>> database) {
        bool anyModify = false;
        do {
            anyModify = false;
            foreach (var type in types) {
                foreach (var method in type.Methods) {

                    if (!method.Parameters.Any(p => IsTileType(p.ParameterType)) && !(method.HasThis && !method.IsConstructor && IsTileType(method.DeclaringType))) {
                        continue;
                    }

                    for (int i = 0; i < method.Parameters.Count; i++) {
                        if (method.Parameters[i].ParameterType is ByReferenceType byReferenceType && IsTileType(byReferenceType.ElementType)) {
                            int index = i + (method.HasThis ? 1 : 0);
                            if (!database.TryGetValue(method.GetIdentifier(), out var indexes)) {
                                anyModify = true;
                                database.Add(method.GetIdentifier(), indexes = [index]);
                            }
                            else {
                                if (indexes.Add(index)) {
                                    anyModify = true;
                                }
                            }
                        }
                    }

                    if (!method.HasBody) {
                        continue;
                    }

                    bool skip = false;
                    foreach (var instruction in method.Body.Instructions.ToArray()) {
                        if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) {
                            var calleeRef = (MethodReference)instruction.Operand!;

                            if (!calleeRef.Name.OrdinalStartsWith("mfwh_")) {
                                continue;
                            }

                            if (database.TryGetValue(calleeRef.GetIdentifier(), out var indexes)) {
                                if (database.TryAdd(method.GetIdentifier(), indexes)) {
                                    anyModify = true;
                                }
                                skip = true;
                            }
                        }
                    }

                    if (skip) {
                        continue;
                    }

                    var jumpTargets = MonoModCommon.Stack.BuildJumpSitesMap(method);

                    Queue<MonoModCommon.Stack.StackTopTypePath> works = new();
                    HashSet<MonoModCommon.Stack.StackTopTypePath> visited = [];

                    foreach (var instruction in method.Body.Instructions) {
                        switch (instruction.OpCode.Code) {
                            case Code.Newobj:
                            case Code.Callvirt:
                            case Code.Call: {
                                    var calleeRef = (MethodReference)instruction.Operand!;
                                    if (database.TryGetValue(calleeRef.GetIdentifier(), out var theseParametersWillBeEdit)) {
                                        foreach (var methodCallPath in MonoModCommon.Stack.AnalyzeParametersSources(method, instruction, jumpTargets)) {
                                            foreach (int index in theseParametersWillBeEdit) {

                                                var paths = MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(
                                                    method,
                                                    methodCallPath.ParametersSources[index].Instructions.Last(),
                                                    jumpTargets);

                                                foreach (var path in paths) {
                                                    works.Enqueue(path);
                                                }
                                            }
                                        }
                                    }
                                    else if (instruction.OpCode == OpCodes.Newobj) {
                                        var ctor = (MethodReference)instruction.Operand!;
                                        var declaringType = ctor.DeclaringType.Resolve();
                                        if (declaringType.BaseType.FullName != "System.MulticastDelegate") {
                                            break;
                                        }

                                        var paths = MonoModCommon.Stack.AnalyzeParametersSources(method, instruction, jumpTargets)
                                            .SelectMany(methodCallPath => MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(method, methodCallPath.ParametersSources[1].Instructions.Last(), jumpTargets));

                                        foreach (var path in paths) {
                                            if (path.Instructions.Length == 1 && path.Instructions[0].OpCode == OpCodes.Ldftn) {
                                                var ldftn = ((MethodReference)path.Instructions[0].Operand).Resolve();

                                                if (database.TryGetValue(ldftn.GetIdentifier(), out var indexes)) {
                                                    HashSet<int> newIndexes;
                                                    if (ldftn.HasThis && !ldftn.IsConstructor) {
                                                        newIndexes = [.. indexes];
                                                        newIndexes.Remove(0);
                                                    }
                                                    else {
                                                        newIndexes = [.. indexes.Select(index => index + 1)];
                                                    }

                                                    var delegateInvoke = declaringType.GetMethod("Invoke");
                                                    if (!database.TryAdd(delegateInvoke.GetIdentifier(), newIndexes)) {
                                                        database[delegateInvoke.GetIdentifier()] = indexes;
                                                    }
                                                    delegateInvoke = declaringType.GetMethod("BeginInvoke");
                                                    if (!database.TryAdd(delegateInvoke.GetIdentifier(), newIndexes)) {
                                                        database[delegateInvoke.GetIdentifier()] = indexes;
                                                    }
                                                }

                                                break;
                                            }
                                        }
                                    }
                                    break;
                                }
                            case Code.Stfld: {
                                    var field = (FieldReference)instruction.Operand!;
                                    if (IsTileType(field.DeclaringType)) {
                                        foreach (var fieldPath in MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, jumpTargets)) {
                                            var paths = MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(
                                                method,
                                                fieldPath.ParametersSources[0].Instructions.Last(),
                                                jumpTargets);
                                            foreach (var path in paths) {
                                                works.Enqueue(path);
                                            }
                                        }
                                    }
                                    break;
                                }
                        }
                    }

                    while (works.Count > 0) {
                        var path = works.Dequeue();
                        if (visited.Add(path)) {

                            var thisInstruction = path.Instructions.First();
                            switch (thisInstruction.OpCode.Code) {
                                case Code.Ldarg:
                                case Code.Ldarg_S:
                                case Code.Ldarg_0:
                                case Code.Ldarg_1:
                                case Code.Ldarg_2:
                                case Code.Ldarg_3:
                                    // Since the 'this' Parameter of a constructor does not need to be pushed onto the stack explicitly by the caller,
                                    // it will not modify the 'this' variable provided by the caller like other instance methods.
                                    // Therefore, we need to handle this case separately and skip it manually
                                    if (thisInstruction.OpCode == OpCodes.Ldarg_0 && method.IsConstructor) {
                                        break;
                                    }

                                    int index = thisInstruction.OpCode.Code switch {
                                        Code.Ldarg_0 => 0,
                                        Code.Ldarg_1 => 1,
                                        Code.Ldarg_2 => 2,
                                        Code.Ldarg_3 => 3,
                                        _ => ((ParameterDefinition)thisInstruction.Operand).Index
                                        + ((method.HasThis) ? 1 : 0)
                                    };

                                    if (!database.TryGetValue(method.GetIdentifier(), out var indexes)) {
                                        anyModify = true;
                                        database.Add(method.GetIdentifier(), indexes = [index]);
                                    }
                                    else {
                                        if (indexes.Add(index)) {
                                            anyModify = true;
                                        }
                                    }
                                    break;

                                case Code.Ldloc:
                                case Code.Ldloc_S:
                                case Code.Ldloc_0:
                                case Code.Ldloc_1:
                                case Code.Ldloc_2:
                                case Code.Ldloc_3:

                                    var variable = MonoModCommon.IL.GetReferencedVariable(method, thisInstruction);

                                    foreach (var instruction in method.Body.Instructions.ToArray()) {
                                        if (instruction.OpCode.Code is
                                            Code.Stloc_0 or
                                            Code.Stloc_1 or
                                            Code.Stloc_2 or
                                            Code.Stloc_3 or
                                            Code.Stloc_S or
                                            Code.Stloc) {

                                            var checkVariable = MonoModCommon.IL.GetReferencedVariable(method, instruction);

                                            if (checkVariable == variable) {
                                                foreach (var sourcePath in MonoModCommon.Stack.AnalyzeInstructionArgsSources(method, instruction, jumpTargets)) {
                                                    foreach (var variablePath in MonoModCommon.Stack.AnalyzeStackTopTypeAllPaths(method, sourcePath.ParametersSources[0].Instructions.Last())) {
                                                        if (!visited.Contains(variablePath)) {
                                                            works.Enqueue(variablePath);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }

                                    variable.VariableType = tileTypeDef.MakeByReferenceType();

                                    break;
                            }
                        }
                    }
                }
            }
        }
        while (anyModify);

        foreach (var type in types) {
            foreach (var method in type.Methods) {
                if (database.TryGetValue(method.GetIdentifier(), out var byRefParamIndexesInculdeThis)) {
                    HashSet<int> byRefParamIndexesExculdeThis = [];
                    foreach (int index in byRefParamIndexesInculdeThis) {
                        int paramIndex = index;
                        if (method.HasThis) {
                            paramIndex -= 1;
                        }

                        if (paramIndex != -1) {
                            byRefParamIndexesExculdeThis.Add(paramIndex);
                            method.Parameters[paramIndex].ParameterType = tileTypeDef.MakeByReferenceType();
                        }
                    }

                    // Intermediate step of transform
                    string newerMethodKey = method.GetIdentifier(true, [], byRefParamIndexesExculdeThis);
                    if (!database.TryAdd(newerMethodKey, byRefParamIndexesInculdeThis)) {
                        database[newerMethodKey] = byRefParamIndexesInculdeThis;
                    }

                    // Final step of transform
                    newerMethodKey = method.GetIdentifier(true, tileNameMap, byRefParamIndexesExculdeThis);
                    if (!database.TryAdd(newerMethodKey, byRefParamIndexesInculdeThis)) {
                        database[newerMethodKey] = byRefParamIndexesInculdeThis;
                    }
                }
            }
        }
    }
}

    public static class Constants
    {
        public const string DelegatesNameSpace = "UnifiedServerProcess.Delegates";
    }
}

namespace OTAPI.UnifiedServerProcess.Optimize.LinkObjects
{
    public class ReversibleLinkedList<TItem>
    {
        public ReversibleLinkedList() { }
        public ReversibleLinkedList(ReversibleLinkedList<TItem> copy) {
            tail = copy.tail;
        }
        Node? tail;
        public void Add(TItem item) {
            tail = new Node(tail, item);
        }
        public TItem[] ReverseToArray() {
            if (tail is null) {
                return [];
            }
            Node? current = tail;
            var result = new TItem[current.currentIndex + 1];

            var indexMax = current.currentIndex;

            while (current != null) {
                result[indexMax - current.currentIndex] = current.item;
                current = current.previous;
            }
            return result;
        }
        public TItem[] ToArray() {
            if (tail is null) {
                return [];
            }
            Node? curr = tail;
            var result = new TItem[curr.currentIndex + 1];
            while (curr != null) {
                result[curr.currentIndex] = curr.item;
                curr = curr.previous;
            }
            return result;
        }
        public int Count => tail?.currentIndex + 1 ?? 0;
        class Node(Node? previous, TItem item)
        {
            public readonly Node? previous = previous;
            public readonly int currentIndex = previous == null ? 0 : previous.currentIndex + 1;
            public readonly TItem item = item;
        }
    }
}


namespace OTAPI.UnifiedServerProcess.Extensions
{
    public static class StringExt
    {
        public static bool OrdinalStartsWith(this string source, string value) => source.StartsWith(value, StringComparison.Ordinal);
        public static bool OrdinalStartsWith(this string source, char value) => source.StartsWith(value);
        public static bool OrdinalEndsWith(this string source, string value) => source.EndsWith(value, StringComparison.Ordinal);
        public static bool OrdinalEndsWith(this string source, char value) => source.EndsWith(value);
    }
}

namespace OTAPI.UnifiedServerProcess.Extensions
{
    public static class EnumExt
    {
        public static unsafe TEnum Remove<TEnum>(this TEnum value, TEnum flag) where TEnum : unmanaged, Enum {
            if (sizeof(TEnum) is 1) {
                byte result = (byte)(*(byte*)&value & ~*(byte*)&flag);
                return *(TEnum*)&result;
            }
            else if (sizeof(TEnum) is 2) {
                ushort result = (ushort)(*(ushort*)&value & ~*(ushort*)&flag);
                return *(TEnum*)&result;
            }
            else if (sizeof(TEnum) is 4) {
                uint result = (uint)(*(uint*)&value & ~*(uint*)&flag);
                return *(TEnum*)&result;
            }
            else if (sizeof(TEnum) is 8) {
                ulong result = (ulong)(*(ulong*)&value & ~*(ulong*)&flag);
                return *(TEnum*)&result;
            }
            else {
                throw new NotSupportedException("Unsupported enum size.");
            }
        }
    }
}


namespace OTAPI.UnifiedServerProcess.Extensions
{
    public static partial class MonoModExtensions
    {
        public static void MakeMethodVirtual(this TypeDefinition type, params MethodDefinition[] ignores) {
            List<MethodDefinition> methods = type.Methods.Where(m => !m.IsConstructor && !m.IsStatic && m.Name != "cctor" && m.Name != "ctor").ToList();
            methods.AddRange(type.Properties.Select(p => p.SetMethod).Where(m => m != null && !m.IsStatic));
            methods.AddRange(type.Properties.Select(p => p.GetMethod).Where(m => m != null && !m.IsStatic));
            foreach (var method in methods) {
                if (ignores.Contains(method)) continue;

                method.IsVirtual = true;
                method.IsNewSlot = true;
            }
        }
        public static void MakeDirect(this TypeDefinition type, out (MethodDefinition wrapped, MethodDefinition origin)[] wrappedMap, params MethodDefinition[] modifies) {
            List<(MethodDefinition from, MethodDefinition to)> maps = [];

            foreach (var method in modifies.Where(m => !m.IsConstructor && !m.IsStatic && m.DeclaringType.FullName == type.FullName)) {
                if (method.Name != "cctor" && method.Name != "ctor"/* && !method.IsVirtual*/) {
                    //CreateForThisType the new replacement method that will take place of the tail method.
                    //So we must ensure we clone to meet the signatures.
                    MethodDefinition wrapped = new MethodDefinition(method.Name, method.Attributes.Remove(Mono.Cecil.MethodAttributes.Virtual), method.ReturnType);
                    wrapped.IsVirtual = false;
                    wrapped.IsNewSlot = false;
                    var instanceMethod = (method.Attributes & Mono.Cecil.MethodAttributes.Static) == 0;


                    //Clone the parameters for the new method
                    if (method.HasParameters) {
                        foreach (var prm in method.Parameters) {
                            wrapped.Parameters.Add(prm);
                        }
                    }

                    //Rename the existing method, and replace all references to it so that the new 
                    //method receives the calls instead.
                    method.Name += "_Direct";

                    maps.Add((wrapped, method));

                    //Get the il processor instance so we can modify IL
                    var il = wrapped.Body.GetILProcessor();

                    //If the callback expects the instance, emit 'this'
                    if (instanceMethod)
                        il.Emit(OpCodes.Ldarg_0);

                    //If there are parameters, add each of them to the stack for the callback
                    if (wrapped.HasParameters) {
                        for (var i = 0; i < wrapped.Parameters.Count; i++) {
                            //Here we are looking at the callback to see if it wants a reference Parameter.
                            //If it does, and it also expects an instance to be passed, we must move the offset
                            //by one to skip the previous ldarg_0 we added before.
                            //var offset = instanceMethod ? 1 : 0;
                            if (method.Parameters[i /*+ offset*/].ParameterType.IsByReference) {
                                il.Emit(OpCodes.Ldarga, wrapped.Parameters[i]);
                            }
                            else il.Emit(OpCodes.Ldarg, wrapped.Parameters[i]);
                        }
                    }

                    //Execute the callback
                    il.Emit(OpCodes.Call, method);

                    //If the end call has a value, pop it for the time being.
                    //In the case of begin callbacks, we use this value to determine
                    //a cancel.
                    //if (method.ReturnType.Name != method.Module.TypeSystem.Void.Name)
                    //    il.Emit(OpCodes.Pop);

                    il.Emit(OpCodes.Ret);

                    //Place the new method in the declaring type of the method we are cloning
                    method.DeclaringType.Methods.Add(wrapped);
                }
            }

            wrappedMap = maps.ToArray();
        }
        private static readonly ConcurrentDictionary<string, bool> _cache = new ConcurrentDictionary<string, bool>();

        public static bool IsTruelyValueType(this TypeReference type) {
            string cacheKey = GetCacheKey(type);

            if (_cache.TryGetValue(cacheKey, out bool cachedResult))
                return cachedResult;

            if (type.IsByReference || type.IsPointer)
                return CacheAndReturn(cacheKey, false);

            // String is normally behave like a value type
            if (!type.IsValueType && type.FullName != type.Module.TypeSystem.String.FullName)
                return CacheAndReturn(cacheKey, false);

            TypeDefinition? typeDef = type.TryResolve();
            if (typeDef is null)
                return CacheAndReturn(cacheKey, false);

            GenericInstanceType? genericInstance = type as GenericInstanceType;

            foreach (FieldDefinition field in typeDef.Fields) {
                if (field.IsStatic)
                    continue;

                TypeReference fieldType = field.FieldType;

                if (genericInstance is not null)
                    fieldType = InflateGenericParameters(fieldType, genericInstance);

                if (fieldType.ContainsGenericParameter)
                    return CacheAndReturn(cacheKey, false);

                if (!fieldType.IsValueType)
                    return CacheAndReturn(cacheKey, false);

                // predefined value types
                if (field.FieldType.FullName == type.FullName)
                    return CacheAndReturn(cacheKey, true);

                if (!fieldType.IsTruelyValueType())
                    return CacheAndReturn(cacheKey, false);
            }

            return CacheAndReturn(cacheKey, true);
        }

        private static string GetCacheKey(TypeReference type) {
            return type.FullName;
        }

        private static bool CacheAndReturn(string cacheKey, bool result) {
            _cache.TryAdd(cacheKey, result);
            return result;
        }

        private static TypeReference InflateGenericParameters(TypeReference fieldType, GenericInstanceType genericInstance) {
            if (fieldType.IsGenericParameter) {
                GenericParameter genericParam = (GenericParameter)fieldType;
                if (genericParam.Type == GenericParameterType.Type &&
                    genericParam.Position < genericInstance.GenericArguments.Count)
                    return genericInstance.GenericArguments[genericParam.Position];
                return fieldType;
            }

            if (fieldType is TypeSpecification typeSpec) {
                TypeReference inflatedElement = InflateGenericParameters(typeSpec.ElementType, genericInstance);
                return typeSpec switch {
                    ArrayType array => new ArrayType(inflatedElement, array.Rank),
                    PointerType ptr => new PointerType(inflatedElement),
                    ByReferenceType byRef => new ByReferenceType(inflatedElement),
                    GenericInstanceType generic => InflateGenericInstance(generic, inflatedElement, genericInstance),
                    RequiredModifierType mod => new RequiredModifierType(mod.ModifierType, inflatedElement),
                    _ => typeSpec
                };
            }

            return fieldType;
        }

        private static GenericInstanceType InflateGenericInstance(GenericInstanceType generic, TypeReference inflatedElement, GenericInstanceType context) {
            GenericInstanceType newGeneric = new GenericInstanceType(inflatedElement);
            foreach (TypeReference arg in generic.GenericArguments)
                newGeneric.GenericArguments.Add(InflateGenericParameters(arg, context));
            return newGeneric;
        }
        public static string GetDebugName(this ParameterDefinition parameter)
            => string.IsNullOrEmpty(parameter.Name) ? "this" : parameter.Name;
        public static bool IsParameterThis(this ParameterDefinition parameter, MethodDefinition method)
            => string.IsNullOrEmpty(parameter.Name) && method.Body.ThisParameter == parameter;
        public static int IndexWithThis(this ParameterDefinition parameter, MethodDefinition method) {
            var index = method.Parameters.IndexOf(parameter);
            if (method.IsStatic || method.IsConstructor) {
                if (index == -1) {
                    throw new ArgumentException("Parameter not found in method", "parameter");
                }
                return index;
            }
            if (index == -1 && method.Body.ThisParameter != parameter) {
                throw new ArgumentException("Parameter not found in method", "parameter");
            }
            return method.Parameters.IndexOf(parameter) + 1;
        }

        public static string GetIdentifier(this FieldReference field) => field.DeclaringType.FullName + "." + field.Name;

        public static bool IsDelegate(this TypeReference type) =>
            type.Name == "MulticastDelegate" ||
            type?.Resolve()?.BaseType?.Name == "MulticastDelegate";

        public static TypeDefinition GetRootDeclaringType(this TypeDefinition type) {
            while (type.DeclaringType is not null) {
                type = type.DeclaringType;
            }
            return type;
        }

        public static TypeDefinition? TryResolve(this TypeReference type) {
            try {
                return type.Resolve();
            }
            catch {
                return null;
            }
        }
        public static MethodDefinition? TryResolve(this MethodReference method) {
            try {
                return method.Resolve();
            }
            catch {
                return null;
            }
        }
        public static FieldDefinition? TryResolve(this FieldReference field) {
            try {
                return field.Resolve();
            }
            catch {
                return null;
            }
        }
        public static FieldDefinition GetField(this TypeDefinition type, string name) {
            return type.Fields.Single((FieldDefinition x) => x.Name == name);
        }

        public static MethodDefinition GetMethod(this TypeDefinition type, string name) {
            return type.Methods.Single((MethodDefinition x) => x.Name == name);
        }
        public static IEnumerable<MethodDefinition> GetRuntimeMethods(this TypeDefinition type, bool includeInterf = false) {
            HashSet<MethodDefinition> visited = [];
            foreach (var md in type.Methods)
                if (visited.Add(md))
                    yield return md;
            var baseType = type.BaseType?.TryResolve();
            if (baseType is not null)
                foreach (var md in baseType.GetRuntimeMethods())
                    if (visited.Add(md))
                        yield return md;
            static IEnumerable<MethodDefinition> GetInterfaceMethods(TypeDefinition type) {
                if (type.IsInterface)
                    foreach (var md in type.Methods)
                        yield return md;
                foreach (var interf in type.Interfaces) {
                    var interfDef = interf.InterfaceType.TryResolve();
                    if (interfDef is not null)
                        foreach (var md in interfDef.Methods)
                            yield return md;
                }
                var baseType = type.BaseType?.TryResolve();
                if (baseType is not null) {
                    foreach (var md in GetInterfaceMethods(baseType))
                        yield return md;
                }
            }
            if (includeInterf) {
                foreach (var md in GetInterfaceMethods(type)) {
                    yield return md;
                }
            }
        }
        public static IEnumerable<(TypeDefinition idef, TypeReference iref)> GetAllInterfaces(this TypeReference type) {
            HashSet<string> visited = [];
            var typeDef = type.TryResolve();
            if (typeDef is null) {
                yield break;
            }
            if (typeDef.IsInterface) {
                yield return (typeDef, type);
            }
            foreach (var interf in typeDef.Interfaces) {
                var interfDef = interf.InterfaceType.TryResolve();
                if (interfDef is not null)
                    if (visited.Add(interfDef.FullName))
                        yield return (interfDef, interf.InterfaceType);
            }
            var baseType = typeDef.BaseType?.TryResolve();
            if (baseType is not null)
                foreach (var interfDef in typeDef.BaseType!.GetAllInterfaces())
                    if (visited.Add(interfDef.idef.FullName))
                        yield return interfDef;
        }

        public static EventDefinition GetEvent(this TypeDefinition type, string name) {
            return type.Events.Single((EventDefinition x) => x.Name == name);
        }

        public static PropertyDefinition GetProperty(this TypeDefinition type, string name) {
            return type.Properties.Single((PropertyDefinition x) => x.Name == name);
        }

        /// <summary>
        /// Inserts instructions before a target instruction, adjusts jump targets and exception blocks
        /// </summary>
        /// <param name="iLProcessor"></param>
        /// <param name="jumpSites"></param>
        /// <param name="target"></param>
        /// <param name="instructions"></param>
        public static void InsertBeforeAndAdjustTargets(this ILProcessor iLProcessor, Dictionary<Instruction, List<Instruction>> jumpSites, Instruction target, params IEnumerable<Instruction> instructions) {
            Instruction? first = instructions.FirstOrDefault();
            if (first is null) {
                return;
            }

            foreach (var instruction in instructions) {
                iLProcessor.InsertBefore(target, instruction);
            }

            if (jumpSites.TryGetValue(target, out var sites)) {
                foreach (var jumpSite in sites) {
                    if (jumpSite.Operand is ILLabel label) {
                        label.Target = first;
                    }
                    else if (jumpSite.Operand is Instruction) {
                        jumpSite.Operand = first;
                    }
                    else {
                        Instruction[] jumpTargets = (Instruction[])jumpSite.Operand;
                        for (int i = 0; i < jumpTargets.Length; i++) {
                            if (jumpTargets[i] == target) {
                                jumpTargets[i] = first;
                            }
                        }
                    }
                }
            }

            if (iLProcessor.Body.HasExceptionHandlers) {
                foreach (var exceptionHandler in iLProcessor.Body.ExceptionHandlers) {
                    if (exceptionHandler.TryStart == target) {
                        exceptionHandler.TryStart = first;
                    }
                    else if (exceptionHandler.HandlerStart == target) {
                        exceptionHandler.HandlerStart = first;
                    }
                    else if (exceptionHandler.FilterStart == target) {
                        exceptionHandler.FilterStart = first;
                    }
                }
            }
        }

        public static Instruction Clone(this Instruction instruction) {
            Instruction clone = Instruction.Create(OpCodes.Nop);
            clone.OpCode = instruction.OpCode;
            clone.Operand = instruction.Operand;
            return clone;
        }
        public static void Clear(this Instruction instruction) {
            instruction.OpCode = OpCodes.Nop;
            instruction.Operand = null;
        }
        public static Instruction CloneAndClear(this Instruction instruction) {
            Instruction clone = Instruction.Create(OpCodes.Nop);
            clone.OpCode = instruction.OpCode;
            clone.Operand = instruction.Operand;

            instruction.OpCode = OpCodes.Nop;
            instruction.Operand = null;
            return clone;
        }

        /// <summary>
        /// Seamlessly inserts a sequence of instructions before the target instruction by modifying the target's content in-place.
        /// <para>
        /// - The original target instruction's opcode and operand will be REPLACED with the FIRST instruction of <paramref name="instructions"/>.<br/>
        /// - The remaining instructions are inserted after the modified target instruction.<br/>
        /// - The original target instruction's content is appended as a NEW instruction at the end of the inserted sequence.<br/>
        /// </para>
        /// All existing branches and exception blocks pointing to the original target will now implicitly point to the new inserted sequence's head, 
        /// while execution flow remains logically equivalent.
        /// </summary>
        /// <param name="iLProcessor">The ILProcessor context for IL manipulation.</param>
        /// <param name="target">[IN/OUT] Reference to the target instruction. After insertion, this reference will point to the NEWLY CREATED instruction containing the original target's content.</param>
        /// <param name="instructions">The instructions to insert. Must contain at least one instruction.</param>
        /// <remarks>
        /// WARNING: The original <paramref name="target"/> reference becomes invalid after this operation. 
        /// Use the updated reference via the 'ref' Parameter for subsequent operations.
        /// </remarks>
        public static void InsertBeforeSeamlessly(this ILProcessor iLProcessor, ref Instruction target, params IEnumerable<Instruction> instructions) {
            InsertBeforeSeamlessly(iLProcessor, ref target, out _, instructions);
        }
        /// <summary>
        /// Seamlessly inserts a sequence of instructions before the target instruction and returns the first inserted instruction.
        /// <para>
        /// - Identical behavior to <see cref="InsertBeforeSeamlessly(ILProcessor, ref Instruction, IEnumerable{Instruction})"/>.<br/>
        /// - Additionally provides the first instruction of the inserted sequence via <paramref name="first"/> Parameter.
        /// </para>
        /// </summary>
        /// <param name="ilProcessor">The ILProcessor context for IL manipulation.</param>
        /// <param name="target">[IN/OUT] Reference to the target instruction. After insertion, this reference will point to the NEWLY CREATED instruction containing the original target's content.</param>
        /// <param name="first">[OUT] The first instruction of the inserted sequence (i.e., the instruction that replaced the original target's content).</param>
        /// <param name="instructions">The instructions to insert. Must contain at least one instruction.</param>
        public static void InsertBeforeSeamlessly(this ILProcessor ilProcessor, ref Instruction target, out Instruction first, params IEnumerable<Instruction> instructions) {
            first = instructions.FirstOrDefault() ?? throw new ArgumentNullException("instructions", "At least one instruction is required.");

            // --- Step 1: Backup Original Target Metadata ---
            var originalOpCode = target.OpCode;
            var originalOperand = target.Operand;

            // --- Step 2: Replace Target with First Inserted Instruction ---
            var firstInserted = first;
            target.OpCode = firstInserted.OpCode;
            target.Operand = firstInserted.Operand;
            first = target;  // Out Parameter points to modified target

            // --- Step 3: Insert Remaining Instructions After Modified Target ---
            Instruction current = target;
            foreach (var instr in instructions.Skip(1)) {
                ilProcessor.InsertAfter(current, instr);
                current = instr;
            }

            // --- Step 4: Append Original Target as New Instruction ---
            Instruction restoredOriginal = Instruction.Create(OpCodes.Nop);
            restoredOriginal.OpCode = originalOpCode;
            restoredOriginal.Operand = originalOperand;
            ilProcessor.InsertAfter(current, restoredOriginal);

            // --- Step 5: Update Target Reference ---
            target = restoredOriginal;  // Caller's ref now points to the restored original
        }
        /// <summary>
        /// Seamlessly removes a specified instruction from a method body while preserving branch targets and exception handler boundaries.
        /// Converts the instruction to Nop if it's referenced elsewhere, otherwise removes it completely.
        /// </summary>
        /// <param name="body">The method body containing the instruction</param>
        /// <param name="jumpSites">Dictionary tracing branch targets (key) and their jump sources (value)</param>
        /// <param name="instruction">The target instruction to remove</param>
        /// <param name="removeFrom">Optional collection to remove from (defaults to body.Instructions)</param>
        public static void RemoveInstructionSeamlessly(this MethodBody body,
            Dictionary<Instruction, List<Instruction>> jumpSites,
            Instruction instruction,
            System.Collections.Generic.ICollection<Instruction>? removeFrom = null) {
            removeFrom ??= body.Instructions;

            // If instruction is a branch target, convert to Nop but keep position
            // to maintain jump offsets while neutralizing its operation
            if (jumpSites.ContainsKey(instruction)) {
                instruction.OpCode = OpCodes.Nop;
                instruction.Operand = null;  // Clear any associated data
                return;
            }

            // Check if instruction is part of any exception handler boundaries
            bool isExceptionBoundary = false;
            if (body.HasExceptionHandlers) {
                foreach (var handler in body.ExceptionHandlers) {
                    // Validate against all handler boundary markers
                    if (handler.TryStart == instruction ||
                        handler.TryEnd == instruction ||
                        handler.HandlerStart == instruction ||
                        handler.HandlerEnd == instruction ||
                        handler.FilterStart == instruction) {
                        isExceptionBoundary = true;
                        break; // Early exit after finding first boundary reference
                    }
                }
            }

            // If part of exception structure, neutralize but preserve position
            if (isExceptionBoundary) {
                instruction.OpCode = OpCodes.Nop;
                instruction.Operand = null;  // Clear exception-related data
                return;
            }

            // Safe to physically remove if not referenced by control flow or exceptions
            removeFrom.Remove(instruction);
        }

        public static string GetSimpleIdentifier(this System.Reflection.MethodBase method, bool withTypeName = true) {
            var type = method.DeclaringType;
            if (type is null && withTypeName) {
                throw new ArgumentException("DeclaringType is null", "method");
            }
            var typeName = withTypeName ? method.DeclaringType!.FullName + "." : "";

            return typeName + method.Name + "(" + string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName)) + ")";
        }
        public static string GetDebugName(this MethodReference method, bool fullTypeName = false) {
            return (fullTypeName ? method.DeclaringType.FullName : method.DeclaringType.Name) + "." + method.Name +
                (method.HasGenericParameters ? "<" + string.Join(",", method.GenericParameters.Select(p => "")) + ">" : "") +
                "(" + string.Join(",", method.Parameters.Select(p => p.ParameterType.Name)) + ")";
        }
    }
}


namespace OTAPI.UnifiedServerProcess.Extensions
{
    public static partial class MonoModExtensions
    {
        // Controls whether malformed generic metadata in identifier formatting should throw or fallback.
        public static bool ThrowOnGetIdentifierMetadataMismatch { get; set; } = true;

        public static string GetIdentifier(this MethodReference method, bool withDeclaring = true)
            => GetIdentifierCore(method, withDeclaring, null, null, null);

        public static string GetIdentifier(this MethodReference method, bool withDeclaring = true, params TypeDefinition[] ignoreParams)
            => GetIdentifierCore(method, withDeclaring, null, null, ignoreParams);

        public static string GetIdentifier(this MethodReference method, bool withDeclaring, Dictionary<string, string> typeNameMap, HashSet<int>? makeByRefIfNot = null)
            => GetIdentifierCore(method, withDeclaring, typeNameMap, makeByRefIfNot, null);

        private static string GetIdentifierCore(
            MethodReference method,
            bool withDeclaring,
            Dictionary<string, string>? typeNameMap,
            HashSet<int>? makeByRefIfNot,
            TypeDefinition[]? ignoreParams) {

            ArgumentNullException.ThrowIfNull(method);

            typeNameMap ??= [];
            makeByRefIfNot ??= [];

            MethodReference methodToFormat = NormalizeMethodReference(method);
            if (withDeclaring && methodToFormat.DeclaringType is null) {
                throw new ArgumentException("DeclaringType is null", "method");
            }

            HashSet<string>? ignoredTypeNames = null;
            if (ignoreParams is { Length: > 0 }) {
                ignoredTypeNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (TypeDefinition ignoreParam in ignoreParams) {
                    ignoredTypeNames.Add(ignoreParam.FullName);
                }
            }

            var identifierBuilder = new StringBuilder();

            if (withDeclaring) {
                identifierBuilder.Append(GetTypeRestrictionName(methodToFormat.DeclaringType!, typeNameMap));
                identifierBuilder.Append("::");
            }

            identifierBuilder.Append(methodToFormat.Name);
            var methodGenericParameterCount = methodToFormat.GenericParameters.Count;
            if (methodGenericParameterCount > 0) {
                identifierBuilder.Append('`');
                identifierBuilder.Append(methodGenericParameterCount);
            }

            var useAnonymousCtorNamedParameters = ShouldUseAnonymousCtorNamedParameters(methodToFormat, out MethodDefinition? anonymousCtorDef);

            identifierBuilder.Append('(');
            var appendedParameterCount = 0;
            for (var i = 0; i < methodToFormat.Parameters.Count; i++) {
                TypeReference parameterType = methodToFormat.Parameters[i].ParameterType;
                if (ShouldIgnoreParameter(parameterType, ignoredTypeNames)) {
                    continue;
                }

                if (appendedParameterCount > 0) {
                    identifierBuilder.Append(',');
                }

                var parameterTypeName = GetParameterTypeName(parameterType, typeNameMap);

                if (makeByRefIfNot.Contains(i) && parameterType is not ByReferenceType) {
                    parameterTypeName += "&";
                }

                if (useAnonymousCtorNamedParameters) {
                    var parameterName = GetAnonymousCtorParameterName(methodToFormat, anonymousCtorDef, i);
                    identifierBuilder.Append(parameterName);
                    identifierBuilder.Append(':');
                }

                identifierBuilder.Append(parameterTypeName);
                appendedParameterCount++;
            }

            identifierBuilder.Append(')');
            return identifierBuilder.ToString();
        }

        private static MethodReference NormalizeMethodReference(MethodReference method) {
            MethodReference candidate = method;

            if (candidate is GenericInstanceMethod genericInstanceMethod) {
                candidate = genericInstanceMethod.ElementMethod;
            }

            if (candidate.DeclaringType is GenericInstanceType && candidate.Resolve() is MethodDefinition resolvedMethod) {
                candidate = resolvedMethod;
            }

            return candidate;
        }

        private static bool ShouldIgnoreParameter(TypeReference parameterType, HashSet<string>? ignoredTypeNames) {
            if (ignoredTypeNames is null || ignoredTypeNames.Count == 0) {
                return false;
            }

            TypeReference checkType = parameterType is ByReferenceType byReferenceType ? byReferenceType.ElementType : parameterType;
            checkType = checkType.GetElementType();
            return ignoredTypeNames.Contains(checkType.FullName);
        }

        private static string GetTypeRestrictionName(TypeReference declaringType, Dictionary<string, string> typeNameMap) {
            TypeReference typeToFormat = declaringType is GenericInstanceType genericInstanceType
                ? genericInstanceType.ElementType
                : declaringType.GetElementType();

            if (typeNameMap.TryGetValue(typeToFormat.FullName, out var mappedName)) {
                return mappedName;
            }

            List<TypeReference> chain = BuildDeclaringTypeChain(typeToFormat);
            var builder = new StringBuilder();

            for (var i = 0; i < chain.Count; i++) {
                TypeReference current = chain[i];

                if (i == 0) {
                    if (!string.IsNullOrEmpty(current.Namespace)) {
                        builder.Append(current.Namespace);
                        builder.Append('.');
                    }
                }
                else {
                    builder.Append('/');
                }

                builder.Append(StripGenericArity(current.Name));

                var arity = GetGenericArity(current);
                if (arity > 0) {
                    builder.Append('`');
                    builder.Append(arity);
                }
            }

            return builder.ToString();
        }

        private static string GetParameterTypeName(TypeReference type, Dictionary<string, string> typeNameMap) {
            if (type is ByReferenceType byReferenceType) {
                return GetParameterTypeName(byReferenceType.ElementType, typeNameMap) + "&";
            }

            if (type is PointerType pointerType) {
                return GetParameterTypeName(pointerType.ElementType, typeNameMap) + "*";
            }

            if (type is ArrayType arrayType) {
                return GetParameterTypeName(arrayType.ElementType, typeNameMap) + GetArraySuffix(arrayType);
            }

            if (type is PinnedType pinnedType) {
                return GetParameterTypeName(pinnedType.ElementType, typeNameMap);
            }

            if (type is OptionalModifierType optionalModifierType) {
                return GetParameterTypeName(optionalModifierType.ElementType, typeNameMap);
            }

            if (type is RequiredModifierType requiredModifierType) {
                return GetParameterTypeName(requiredModifierType.ElementType, typeNameMap);
            }

            if (type is SentinelType sentinelType) {
                return GetParameterTypeName(sentinelType.ElementType, typeNameMap);
            }

            if (type is GenericParameter genericParameter) {
                return genericParameter.Type == GenericParameterType.Method
                    ? $"!!{genericParameter.Position}"
                    : $"!{genericParameter.Position}";
            }

            if (typeNameMap.TryGetValue(type.FullName, out var exactMappedTypeName)) {
                return NormalizeTypeNameForParameter(exactMappedTypeName);
            }

            if (type is GenericInstanceType genericInstanceType) {
                return FormatGenericInstanceParameterType(genericInstanceType, typeNameMap);
            }

            TypeReference elementType = type.GetElementType();
            if (typeNameMap.TryGetValue(elementType.FullName, out var mappedElementTypeName)) {
                return NormalizeTypeNameForParameter(mappedElementTypeName);
            }

            return FormatNamedTypeForParameter(type, typeNameMap);
        }

        private static string FormatGenericInstanceParameterType(GenericInstanceType genericInstanceType, Dictionary<string, string> typeNameMap) {
            TypeReference elementType = genericInstanceType.ElementType.GetElementType();

            if (typeNameMap.TryGetValue(elementType.FullName, out var mappedElementTypeName)) {
                return AppendGenericArgumentsToMappedType(
                    NormalizeTypeNameForParameter(mappedElementTypeName),
                    genericInstanceType.GenericArguments,
                    typeNameMap);
            }

            List<TypeReference> chain = BuildDeclaringTypeChain(elementType);
            var builder = new StringBuilder();
            var nextArgIndex = 0;

            for (var i = 0; i < chain.Count; i++) {
                TypeReference part = chain[i];

                if (i == 0) {
                    if (!string.IsNullOrEmpty(part.Namespace)) {
                        builder.Append(part.Namespace);
                        builder.Append('.');
                    }
                }
                else {
                    builder.Append('.');
                }

                builder.Append(StripGenericArity(part.Name));

                var partArity = GetGenericArity(part);
                if (partArity <= 0) {
                    continue;
                }

                builder.Append('<');

                for (var argOffset = 0; argOffset < partArity; argOffset++) {
                    if (argOffset > 0) {
                        builder.Append(',');
                    }

                    if (TryConsumeGenericArgument(
                        genericInstanceType,
                        part,
                        argOffset,
                        ref nextArgIndex,
                        out TypeReference? consumedArgumentType)) {
                        builder.Append(GetParameterTypeName(consumedArgumentType!, typeNameMap));
                    }
                    else {
                        HandleMetadataMismatch(
                            $"Missing generic argument while formatting '{genericInstanceType.FullName}'. " +
                            $"Expected slot index {argOffset} for part '{part.FullName}', " +
                            $"explicit arguments consumed {nextArgIndex}/{genericInstanceType.GenericArguments.Count}.");
                        builder.Append('!');
                        builder.Append(argOffset);
                    }
                }

                builder.Append('>');
            }

            if (nextArgIndex < genericInstanceType.GenericArguments.Count) {
                HandleMetadataMismatch(
                    $"Redundant generic arguments while formatting '{genericInstanceType.FullName}'. " +
                    $"Consumed {nextArgIndex}, actual {genericInstanceType.GenericArguments.Count}.");
                builder.Append('<');
                for (var i = nextArgIndex; i < genericInstanceType.GenericArguments.Count; i++) {
                    if (i > nextArgIndex) {
                        builder.Append(',');
                    }

                    builder.Append(GetParameterTypeName(genericInstanceType.GenericArguments[i], typeNameMap));
                }
                builder.Append('>');
            }

            return builder.ToString();
        }

        private static bool TryConsumeGenericArgument(
            GenericInstanceType genericInstanceType,
            TypeReference typePart,
            int slotIndex,
            ref int nextExplicitArgumentIndex,
            out TypeReference? argumentType) {

            if (nextExplicitArgumentIndex < genericInstanceType.GenericArguments.Count) {
                argumentType = genericInstanceType.GenericArguments[nextExplicitArgumentIndex];
                nextExplicitArgumentIndex++;
                return true;
            }

            if (slotIndex < typePart.GenericParameters.Count) {
                argumentType = typePart.GenericParameters[slotIndex];
                return true;
            }

            argumentType = null;
            return false;
        }

        private static string AppendGenericArgumentsToMappedType(
            string mappedTypeName,
            System.Collections.Generic.ICollection<TypeReference> genericArguments,
            Dictionary<string, string> typeNameMap) {

            if (genericArguments.Count == 0) {
                return mappedTypeName;
            }

            var builder = new StringBuilder(mappedTypeName);
            builder.Append('<');

            var index = 0;
            foreach (TypeReference argument in genericArguments) {
                if (index > 0) {
                    builder.Append(',');
                }

                builder.Append(GetParameterTypeName(argument, typeNameMap));
                index++;
            }

            builder.Append('>');
            return builder.ToString();
        }

        private static string FormatNamedTypeForParameter(TypeReference type, Dictionary<string, string> typeNameMap) {
            TypeReference elementType = type.GetElementType();
            var simpleName = StripGenericArity(elementType.Name);

            if (elementType.DeclaringType is not null) {
                return GetParameterTypeName(elementType.DeclaringType, typeNameMap) + "." + simpleName;
            }

            if (typeNameMap.TryGetValue(elementType.FullName, out var mappedElementTypeName)) {
                return NormalizeTypeNameForParameter(mappedElementTypeName);
            }

            if (!string.IsNullOrEmpty(elementType.Namespace)) {
                return elementType.Namespace + "." + simpleName;
            }

            return simpleName;
        }

        private static bool ShouldUseAnonymousCtorNamedParameters(MethodReference method, out MethodDefinition? anonymousCtorDef) {
            anonymousCtorDef = null;

            if (method.Name != ".ctor") {
                return false;
            }

            TypeReference? declaringType = method.DeclaringType;
            if (declaringType is null || !declaringType.Name.OrdinalStartsWith("<>f__AnonymousType")) {
                return false;
            }

            TypeDefinition? declaringTypeDef = declaringType.Resolve();
            if (declaringTypeDef is null) {
                HandleMetadataMismatch($"Failed to resolve anonymous declaring type '{declaringType.FullName}'.");
                return false;
            }

            var instanceCtorCount = 0;
            foreach (MethodDefinition? maybeCtor in declaringTypeDef.Methods) {
                if (maybeCtor.IsConstructor && !maybeCtor.IsStatic) {
                    instanceCtorCount++;
                    if (instanceCtorCount > 1) {
                        break;
                    }
                }
            }

            if (instanceCtorCount <= 1) {
                return false;
            }

            // MethodReference often loses constructor parameter names; resolve definition for stable names.
            anonymousCtorDef = method.Resolve();
            if (anonymousCtorDef is null) {
                HandleMetadataMismatch($"Failed to resolve anonymous constructor '{declaringType.FullName}::{method.Name}'.");
            }

            return true;
        }

        private static string GetAnonymousCtorParameterName(MethodReference method, MethodDefinition? anonymousCtorDef, int parameterIndex) {
            var parameterName = method.Parameters[parameterIndex].Name;
            if (string.IsNullOrEmpty(parameterName)
                && anonymousCtorDef is not null
                && parameterIndex < anonymousCtorDef.Parameters.Count) {
                parameterName = anonymousCtorDef.Parameters[parameterIndex].Name;
            }

            if (!string.IsNullOrEmpty(parameterName)) {
                return parameterName;
            }

            HandleMetadataMismatch(
                $"Anonymous constructor parameter name is missing: '{method.DeclaringType?.FullName}::{method.Name}' index {parameterIndex}.");
            return "arg" + parameterIndex;
        }

        private static string NormalizeTypeNameForParameter(string typeName) {
            if (string.IsNullOrEmpty(typeName)) {
                return typeName;
            }

            var normalized = typeName.Replace('/', '.');
            var builder = new StringBuilder(normalized.Length);

            for (var i = 0; i < normalized.Length; i++) {
                var current = normalized[i];
                if (current != '`') {
                    builder.Append(current);
                    continue;
                }

                var digitStart = i + 1;
                var digitLength = 0;
                while (digitStart + digitLength < normalized.Length && char.IsDigit(normalized[digitStart + digitLength])) {
                    digitLength++;
                }

                if (digitLength == 0) {
                    builder.Append(current);
                    continue;
                }

                var nextIndex = digitStart + digitLength;
                if (nextIndex == normalized.Length || IsTypeNameDelimiter(normalized[nextIndex])) {
                    i = nextIndex - 1;
                    continue;
                }

                builder.Append(current);
            }

            return builder.ToString();
        }

        private static string GetArraySuffix(ArrayType arrayType) {
            if (arrayType.Rank <= 1) {
                return "[]";
            }

            return "[" + new string(',', arrayType.Rank - 1) + "]";
        }

        private static List<TypeReference> BuildDeclaringTypeChain(TypeReference type) {
            var chain = new List<TypeReference>();

            for (TypeReference? current = type; current is not null; current = current.DeclaringType) {
                TypeReference unwrapped = current is GenericInstanceType genericInstanceType
                    ? genericInstanceType.ElementType
                    : current.GetElementType();

                chain.Add(unwrapped);
            }

            chain.Reverse();
            return chain;
        }

        private static int GetGenericArity(TypeReference type) {
            if (TryParseTrailingGenericArity(type.Name, out _, out var parsedArity)) {
                return parsedArity;
            }

            return type.HasGenericParameters ? type.GenericParameters.Count : 0;
        }

        private static string StripGenericArity(string typeName) {
            return TryParseTrailingGenericArity(typeName, out var tickIndex, out _)
                ? typeName[..tickIndex]
                : typeName;
        }

        private static bool TryParseTrailingGenericArity(string typeName, out int tickIndex, out int arity) {
            tickIndex = typeName.LastIndexOf('`');
            arity = 0;

            if (tickIndex < 0 || tickIndex + 1 >= typeName.Length) {
                return false;
            }

            ReadOnlySpan<char> span = typeName.AsSpan(tickIndex + 1);
            if (!int.TryParse(span, out arity)) {
                return false;
            }

            return true;
        }

        private static bool IsTypeNameDelimiter(char c) {
            return c is '.' or '<' or '>' or ',' or '[' or ']' or '&' or '*' or '+';
        }

        private static void HandleMetadataMismatch(string message) {
            if (ThrowOnGetIdentifierMetadataMismatch) {
                throw new InvalidOperationException(message);
            }
        }
    }
}


namespace OTAPI.UnifiedServerProcess.Commons
{
    public static partial class MonoModCommon
    {
        public static class Stack
        {
            private struct AnalysisContext
            {
                public Instruction CurrentInstruction;
                public int TargetPosition;

                public AnalysisContext(Instruction current, int position) {
                    CurrentInstruction = current;
                    TargetPosition = position;
                }
            }

            /// <summary>
            /// 
            /// </summary>
            /// <param name="caller">Executing method</param>
            /// <param name="afterThisExec">Analyze after this instruction</param>
            /// <returns>Return all possible uses of the top value on the stack/returns>
            public static Instruction[] TraceStackValueConsumers(MethodDefinition caller, Instruction afterThisExec) {
                HashSet<(Instruction, int)> visited = new HashSet<(Instruction, int)>();
                HashSet<Instruction> results = new HashSet<Instruction>();
                Stack<AnalysisContext> workStack = new Stack<AnalysisContext>();

                Instruction initialInstruction = afterThisExec.Next;

                if (initialInstruction is null) return [];

                workStack.Push(new AnalysisContext(initialInstruction, 0));

                while (workStack.Count > 0) {
                    var ctx = workStack.Pop();

                    if (visited.Contains((ctx.CurrentInstruction, ctx.TargetPosition)))
                        continue;
                    visited.Add((ctx.CurrentInstruction, ctx.TargetPosition));

                    int popCount = GetPopCount(caller.Body, ctx.CurrentInstruction);
                    int pushCount = GetPushCount(caller.Body, ctx.CurrentInstruction);
                    int currentPosition = ctx.TargetPosition;

                    if (currentPosition < popCount) {
                        if (ctx.CurrentInstruction.OpCode == OpCodes.Dup) {
                            foreach (var next in GetNextInstructions(ctx.CurrentInstruction)) {
                                if (next is null) continue;
                                workStack.Push(new AnalysisContext(next, 0));
                                workStack.Push(new AnalysisContext(next, 1));
                            }
                        }
                        else {
                            results.Add(ctx.CurrentInstruction);
                        }
                    }
                    else {
                        int newPosition = currentPosition - popCount + pushCount;
                        foreach (var next in GetNextInstructions(ctx.CurrentInstruction)) {
                            if (next != null)
                                workStack.Push(new AnalysisContext(next, newPosition));
                        }
                    }
                }

                return results.ToArray();
            }
            public static Instruction[] TraceStackValueFinalConsumers(MethodDefinition caller, Instruction afterThisExec) {
                List<Instruction> results = [];
                Stack<Instruction> works = [];
                works.Push(afterThisExec);

                while (works.Count > 0) {
                    var current = works.Pop();
                    var usages = TraceStackValueConsumers(caller, current);
                    foreach (var usage in usages) {
                        if (MonoModCommon.Stack.GetPushCount(caller.Body, usage) > 0) {
                            works.Push(usage);
                        }
                        else {
                            results.Add(usage);
                        }
                    }
                }
                return results.ToArray();
            }

            private static IEnumerable<Instruction> GetNextInstructions(Instruction current) {
                switch (current.OpCode.FlowControl) {
                    case FlowControl.Branch:
                        yield return (Instruction)current.Operand;
                        break;

                    case FlowControl.Cond_Branch:
                        yield return (Instruction)current.Operand;
                        yield return current.Next;
                        break;

                    case FlowControl.Return:
                    case FlowControl.Throw:
                        yield break;

                    default:
                        yield return current.Next;
                        break;
                }
            }
            public static TypeReference? AnalyzeStackTopType(MethodDefinition caller, Instruction afterThisExec, Dictionary<Instruction, List<Instruction>>? cachedJumpSitess = null) {
                // Verify target is within the method's instructions
                var instructions = caller.Body.Instructions;
                //int targetIndex = instructions.IndexOf(afterThisExec);
                //if (targetIndex == -1)
                //    throw new ArgumentException("Target instruction is not part of the method body.");

                cachedJumpSitess ??= BuildJumpSitesMap(caller);

                // initialize analysis queue
                Stack<(Instruction current, int stackBalance, HashSet<Instruction> visited)> workStack = new Stack<(Instruction current, int stackBalance, HashSet<Instruction> visited)>();

                workStack.Push((afterThisExec, -1, []));

                TypeReference? type = null;

                // var visited = new HashSet<(Instruction, int)>();

                while (workStack.Count > 0) {
                    var (current, stackBalance, visited) = workStack.Pop();

                    // Check for visited state to prevent loops

                    if (visited.Contains(current)) continue;
                    visited.Add(current);

                    var newerBalance = stackBalance + GetPushCount(caller.Body, current) - GetPopCount(caller.Body, current);

                    var addCount = GetPushCount(caller.Body, current);
                    if (addCount > 1) {
                        addCount = GetPushCount(caller.Body, current) - GetPopCount(caller.Body, current);
                        if (addCount <= 0) {
                            addCount = 1;
                        }
                    }

                    if (stackBalance + addCount >= 0) {
                        if (current.OpCode != OpCodes.Dup) {
                            type = GetPushType(current, caller, cachedJumpSitess);
                            if (type is not null) {
                                break;
                            }
                            else {
                                continue;
                            }
                        }
                        else {
                            newerBalance = -1;
                        }
                    }

                    if (cachedJumpSitess.TryGetValue(current, out var jumpSitess)) {
                        foreach (var source in jumpSitess) {
                            workStack.Push((source, newerBalance, [.. visited]));
                        }
                    }

                    // linear backtracing
                    if (current.Previous != null) {
                        if (!IsTerminatorInstruction(current.Previous)) {
                            workStack.Push((current.Previous, newerBalance, visited));
                        }
                        else if (newerBalance == -1 && IsTryEndInstruction(caller.Body, current.Previous, out type)) {
                            break;
                        }
                    }
                }

                return type;
            }
            public static StackTopTypePath[] AnalyzeStackTopTypeAllPaths(MethodDefinition caller, Instruction afterThisExec, Dictionary<Instruction, List<Instruction>>? cachedJumpSitess = null) {
                var instructions = caller.Body.Instructions;
                //int targetIndex = instructions.IndexOf(afterThisExec);
                //if (targetIndex == -1)
                //    throw new ArgumentException("Target instruction is not part of the method body.");

                cachedJumpSitess ??= BuildJumpSitesMap(caller);

                // initialize analysis queue
                Stack<(ReversibleLinkedList<Instruction> path, Instruction current, int stackBalance, HashSet<Instruction> visited)> workStack = new Stack<(ReversibleLinkedList<Instruction> path, Instruction current, int stackBalance, HashSet<Instruction> visited)>();

                workStack.Push((new(), afterThisExec, -1, []));

                List<StackTopTypePath> paths = [];
                // var visited = new HashSet<(Instruction, int)>();

                while (workStack.Count > 0) {
                    var (path, current, stackBalance, visited) = workStack.Pop();

                    path.Add(current);

                    if (visited.Contains(current)) continue;
                    visited.Add(current);

                    var newerBalance = stackBalance + GetPushCount(caller.Body, current) - GetPopCount(caller.Body, current);

                    var addCount = GetPushCount(caller.Body, current);
                    if (addCount > 1) {
                        addCount = GetPushCount(caller.Body, current) - GetPopCount(caller.Body, current);
                        if (addCount <= 0) {
                            addCount = 1;
                        }
                    }

                    if (stackBalance + addCount >= 0) {
                        if (current.OpCode != OpCodes.Dup) {
                            var type = GetPushType(current, caller, cachedJumpSitess);
                            paths.Add(new StackTopTypePath(type, current) { Instructions = path.ToArray() });
                            if (paths.Count > 256) {
                                workStack.Clear();
                            }
                            continue;
                        }
                        else {
                            newerBalance = -1;
                        }
                    }

                    if (cachedJumpSitess.TryGetValue(current, out var jumpSitess)) {
                        foreach (var source in jumpSitess) {
                            workStack.Push((new(path), source, newerBalance, [.. visited]));
                        }
                    }

                    // linear backtracing
                    if (current.Previous != null) {
                        if (!IsTerminatorInstruction(current.Previous)) {
                            workStack.Push((new(path), current.Previous, newerBalance, visited));
                        }
                        else if (newerBalance == -1 && IsTryEndInstruction(caller.Body, current.Previous, out var exceptionType)) {
                            path.Add(current.Previous);
                            paths.Add(new StackTopTypePath(exceptionType, current.Previous) { Instructions = path.ToArray() });
                            if (paths.Count > 256) {
                                workStack.Clear();
                            }
                            continue;
                        }
                    }
                }

                HashSet<StackTopTypePath> stackTopTypePaths = [];

                foreach (var path in paths) {
                    path.Instructions = [.. path.Instructions.Where(inst => !IsStackEffectFree(caller.Body, inst)).Reverse()];
                    stackTopTypePaths.Add(path);
                }
                return [.. stackTopTypePaths];
            }
            public static bool CheckSinglePredecessor(
                MethodDefinition method,
                Instruction upperBound,
                Instruction lowerBound,
                Dictionary<Instruction, List<Instruction>>? cachedJumpSitess = null) {

                cachedJumpSitess ??= BuildJumpSitesMap(method);
                HashSet<Instruction> allowsForward = [];
                HashSet<Instruction> allowsBackward = [];
                HashSet<Instruction> allows = [];
                // Step 1: Build allowed instruction set
                var currentForward = lowerBound;
                var currentBackward = lowerBound;

                while (currentForward != null && currentBackward != null) {
                    if (currentForward is not null) {
                        allowsForward.Add(currentForward);
                        currentForward = currentForward.Previous;
                    }

                    if (currentBackward is not null) {
                        allowsBackward.Add(currentBackward);
                        currentBackward = currentBackward.Next;
                    }

                    if (currentForward == upperBound) {
                        allows = allowsForward;
                        break;
                    }

                    if (currentBackward == upperBound) {
                        (upperBound, lowerBound) = (lowerBound, upperBound);
                        allows = allowsBackward;
                        break;
                    }
                }

                if (allows.Count == 0) {
                    throw new ArgumentException("Invalid bounds.");
                }

                // Step 2: Traverse all possible predecessors
                HashSet<Instruction> visited = [];
                Stack<Instruction> works = new();
                bool reachUpperBound = false;
                works.Push(lowerBound);

                while (works.Count > 0) {
                    var work = works.Pop();
                    if (visited.Contains(work)) continue;
                    visited.Add(work);

                    // Check jump sources
                    if (cachedJumpSitess.TryGetValue(work, out var jumpSites)) {
                        foreach (var site in jumpSites) {
                            if (!allows.Contains(site)) {
                                // External jump source detected
                                return false;
                            }
                            works.Push(site);
                        }
                    }

                    // Check previous instruction
                    var previous = work.Previous;
                    if (previous is null || IsUnreachablePredecessor(previous)) {
                        // Dead end, stop tracing this path
                        continue;
                    }
                    if (previous == upperBound) {
                        reachUpperBound = true;
                        continue;
                    }

                    works.Push(previous);
                }

                return reachUpperBound;
            }

            static void FindEndpoints(IEnumerable<Instruction> nodes, out Instruction min, out Instruction max, out HashSet<Instruction> block) {
                HashSet<Instruction> nodeSet = new HashSet<Instruction>(nodes);

                if (nodeSet.Count == 0) throw new ArgumentException("Node set must contain at least one node");

                Instruction start = nodeSet.First();
                nodeSet.Remove(start);
                block = [start];

                min = start;
                max = start;

                Instruction leftCursor = start;
                HashSet<Instruction> leftCollected = [];
                Instruction rightCursor = start;
                HashSet<Instruction> rightCollected = [];

                while (nodeSet.Count > 0) {
                    if (leftCursor.Previous != null) {
                        leftCursor = leftCursor.Previous;

                        leftCollected.Add(leftCursor);

                        if (nodeSet.Remove(leftCursor)) {
                            min = leftCursor;
                            foreach (var inst in leftCollected) {
                                block.Add(inst);
                            }
                        }
                    }

                    if (nodeSet.Count == 0) break;

                    if (rightCursor.Next != null) {
                        rightCursor = rightCursor.Next;

                        rightCollected.Add(rightCursor);

                        if (nodeSet.Remove(rightCursor)) {
                            max = rightCursor;
                            foreach (var inst in rightCollected) {
                                block.Add(inst);
                            }
                        }
                    }
                }
            }
            public static bool CheckSinglePredecessor(
                MethodDefinition method,
                Instruction[] bound,
                out Instruction upperBound,
                out Instruction lowerBound,
                Dictionary<Instruction, List<Instruction>>? cachedJumpSitess = null) {

                if (bound.Length < 2) {
                    throw new ArgumentException("Invalid bounds.");
                }

                FindEndpoints(bound, out upperBound, out lowerBound, out var block);

                cachedJumpSitess ??= BuildJumpSitesMap(method);

                // Step 2: Traverse all possible predecessors
                HashSet<Instruction> visited = [];
                Stack<Instruction> works = new();
                bool reachUpperBound = false;

                works.Push(lowerBound);

                while (works.Count > 0) {
                    var work = works.Pop();
                    if (visited.Contains(work)) continue;
                    visited.Add(work);

                    // Check jump sources
                    if (cachedJumpSitess.TryGetValue(work, out var jumpSites)) {
                        foreach (var site in jumpSites) {
                            if (!block.Contains(site)) {
                                // External jump source detected
                                return false;
                            }
                            works.Push(site);
                        }
                    }

                    // Check previous instruction
                    var previous = work.Previous;
                    if (previous is null || IsUnreachablePredecessor(previous)) {
                        // Dead end, stop tracing this path
                        continue;
                    }

                    if (previous == upperBound) {
                        reachUpperBound = true;
                        continue;
                    }

                    works.Push(previous);
                }

                return reachUpperBound;
            }

            private static bool IsUnreachablePredecessor(Instruction instr) {
                return instr.OpCode.FlowControl switch {
                    FlowControl.Return => true, // ret
                    FlowControl.Throw => true,  // throw
                    FlowControl.Branch => true, // br/br.s
                    _ => false
                };
            }
            public static TypeReference? GetPushType(Instruction instruction, MethodDefinition method, Dictionary<Instruction, List<Instruction>>? cachedJumpSites = null) {
                switch (instruction.OpCode.Code) {
                    // Load
                    case Code.Ldloc_0:
                    case Code.Ldloc_1:
                    case Code.Ldloc_2:
                    case Code.Ldloc_3:
                    case Code.Ldloc_S:
                    case Code.Ldloc:
                        var variable = IL.GetReferencedVariable(method, instruction);
                        return variable.VariableType;
                    case Code.Ldarg_0:
                    case Code.Ldarg_1:
                    case Code.Ldarg_2:
                    case Code.Ldarg_3:
                    case Code.Ldarg_S:
                    case Code.Ldarg:
                        var parameter = IL.GetReferencedParameter(method, instruction);
                        return parameter.ParameterType;
                    case Code.Ldfld:
                        FieldReference field = (FieldReference)instruction.Operand;
                        return field.FieldType;
                    case Code.Ldsfld:
                        field = (FieldReference)instruction.Operand;
                        return field.FieldType;
                    case Code.Call:
                    case Code.Callvirt:
                        MethodReference methodCall = (MethodReference)instruction.Operand;
                        return IL.GetMethodReturnType(methodCall, method);
                    case Code.Calli:
                        CallSite sig = (CallSite)instruction.Operand;
                        return sig.ReturnType;
                    case Code.Newobj:
                        MethodReference ctor = (MethodReference)instruction.Operand;
                        return ctor.DeclaringType;
                    case Code.Newarr:
                        return new ArrayType((TypeReference)instruction.Operand);
                    case Code.Ldnull:
                        return null; // Null reference
                    case Code.Ldstr:
                        return method.Module.TypeSystem.String;
                    case Code.Ldc_I4:
                    case Code.Ldc_I4_S:
                    case Code.Ldc_I4_0:
                    case Code.Ldc_I4_1:
                    case Code.Ldc_I4_2:
                    case Code.Ldc_I4_3:
                    case Code.Ldc_I4_4:
                    case Code.Ldc_I4_5:
                    case Code.Ldc_I4_6:
                    case Code.Ldc_I4_7:
                    case Code.Ldc_I4_8:
                    case Code.Ldc_I4_M1:
                        return method.Module.TypeSystem.Int32;
                    case Code.Ldc_I8:
                        return method.Module.TypeSystem.Int64;
                    case Code.Ldc_R4:
                    case Code.Ldc_R8:
                        return method.Module.TypeSystem.Double;

                    // number calculate

                    // fixed return Int32
                    case Code.Add_Ovf:
                    case Code.Sub_Ovf:
                    case Code.Mul_Ovf:
                        return method.Module.TypeSystem.Int32;

                    // fixed return UInt32
                    case Code.Add_Ovf_Un:
                    case Code.Sub_Ovf_Un:
                    case Code.Mul_Ovf_Un:
                        return method.Module.TypeSystem.UInt32;

                    case Code.Shl:
                    case Code.Shr:
                    case Code.Shr_Un:
                    case Code.And:
                    case Code.Or:
                    case Code.Xor:
                    case Code.Not:
                    case Code.Add:
                    case Code.Sub:
                    case Code.Mul:
                    case Code.Div:
                    case Code.Div_Un:
                    case Code.Rem:
                    case Code.Rem_Un:
                    case Code.Neg:
                        var calculatePaths = Stack.AnalyzeInstructionArgsSources(method, instruction, cachedJumpSites);
                        var operandType = Stack.AnalyzeStackTopType(method, calculatePaths[0].ParametersSources[0].Instructions.Last(), cachedJumpSites);
                        return operandType;

                    // number type convert
                    case Code.Conv_I1: return method.Module.TypeSystem.SByte;
                    case Code.Conv_I2: return method.Module.TypeSystem.Int16;
                    case Code.Conv_I4: return method.Module.TypeSystem.Int32;
                    case Code.Conv_I8: return method.Module.TypeSystem.Int64;
                    case Code.Conv_U1: return method.Module.TypeSystem.Byte;
                    case Code.Conv_U2: return method.Module.TypeSystem.UInt16;
                    case Code.Conv_U4: return method.Module.TypeSystem.UInt32;
                    case Code.Conv_U8: return method.Module.TypeSystem.UInt64;
                    case Code.Conv_R4: return method.Module.TypeSystem.Single;
                    case Code.Conv_R8: return method.Module.TypeSystem.Double;
                    case Code.Conv_I: return method.Module.TypeSystem.IntPtr;
                    case Code.Conv_U: return method.Module.TypeSystem.UIntPtr;
                    case Code.Conv_Ovf_I1: return method.Module.TypeSystem.SByte;
                    case Code.Conv_Ovf_I2: return method.Module.TypeSystem.Int16;
                    case Code.Conv_Ovf_I4: return method.Module.TypeSystem.Int32;
                    case Code.Conv_Ovf_I8: return method.Module.TypeSystem.Int64;
                    case Code.Conv_Ovf_U1: return method.Module.TypeSystem.Byte;
                    case Code.Conv_Ovf_U2: return method.Module.TypeSystem.UInt16;
                    case Code.Conv_Ovf_U4: return method.Module.TypeSystem.UInt32;
                    case Code.Conv_Ovf_U8: return method.Module.TypeSystem.UInt64;
                    case Code.Conv_Ovf_I: return method.Module.TypeSystem.IntPtr;
                    case Code.Conv_Ovf_U: return method.Module.TypeSystem.UIntPtr;

                    // fixed return Single
                    case Code.Conv_R_Un: return method.Module.TypeSystem.Single;

                    // reference type convert
                    case Code.Castclass:
                    case Code.Unbox:
                    case Code.Unbox_Any:
                        return (TypeReference)instruction.Operand;
                    case Code.Box:
                        return method.Module.TypeSystem.Object;
                    case Code.Isinst:
                        return method.Module.TypeSystem.Boolean;

                    // number compare
                    case Code.Ceq:
                    case Code.Cgt:
                    case Code.Cgt_Un:
                    case Code.Clt:
                    case Code.Clt_Un:
                        return method.Module.TypeSystem.Boolean;

                    // array access
                    case Code.Ldlen: return method.Module.TypeSystem.Int32;
                    case Code.Ldelem_I1: return method.Module.TypeSystem.SByte;
                    case Code.Ldelem_U1: return method.Module.TypeSystem.Byte;
                    case Code.Ldelem_I2: return method.Module.TypeSystem.Int16;
                    case Code.Ldelem_U2: return method.Module.TypeSystem.UInt16;
                    case Code.Ldelem_I4: return method.Module.TypeSystem.Int32;
                    case Code.Ldelem_U4: return method.Module.TypeSystem.UInt32;
                    case Code.Ldelem_I8: return method.Module.TypeSystem.Int64;
                    case Code.Ldelem_R4: return method.Module.TypeSystem.Single;
                    case Code.Ldelem_R8: return method.Module.TypeSystem.Double;
                    case Code.Ldelem_I: return method.Module.TypeSystem.IntPtr;
                    case Code.Ldelem_Any: return (TypeReference)instruction.Operand;
                    case Code.Ldelem_Ref:
                        var arrayAccessPaths = AnalyzeInstructionArgsSources(method, instruction, cachedJumpSites).First();
                        var arrayInstructions = arrayAccessPaths.ParametersSources[0].Instructions;
                        var arrayType = AnalyzeStackTopType(method, arrayInstructions.Last(), cachedJumpSites);
                        if (arrayType is ArrayType at) {
                            return at.ElementType;
                        }
                        throw new NotSupportedException($"Could not analyze array type: {arrayType}");

                    case Code.Ldind_I1: return method.Module.TypeSystem.SByte;
                    case Code.Ldind_I2: return method.Module.TypeSystem.Int16;
                    case Code.Ldind_I4: return method.Module.TypeSystem.Int32;
                    case Code.Ldind_I8: return method.Module.TypeSystem.Int64;
                    case Code.Ldind_U1: return method.Module.TypeSystem.Byte;
                    case Code.Ldind_U2: return method.Module.TypeSystem.UInt16;
                    case Code.Ldind_U4: return method.Module.TypeSystem.UInt32;
                    case Code.Ldind_R4: return method.Module.TypeSystem.Single;
                    case Code.Ldind_R8: return method.Module.TypeSystem.Double;
                    case Code.Ldind_I: return method.Module.TypeSystem.IntPtr;
                    case Code.Ldind_Ref:
                        var referencePaths = AnalyzeInstructionArgsSources(method, instruction, cachedJumpSites).First();
                        var referenceInstructions = referencePaths.ParametersSources[0].Instructions;
                        ByReferenceType referenceType = (ByReferenceType)AnalyzeStackTopType(method, referenceInstructions.Last(), cachedJumpSites)!;
                        return referenceType.ElementType;

                    case Code.Ldobj: return (TypeReference)instruction.Operand;

                    // memory access
                    case Code.Ldftn:
                    case Code.Ldvirtftn:
                        return method.Module.TypeSystem.IntPtr;
                    case Code.Ldarga:
                    case Code.Ldarga_S:
                        var param = IL.GetReferencedParameter(method, instruction);
                        return new ByReferenceType(param.ParameterType);
                    case Code.Ldloca:
                    case Code.Ldloca_S:
                        var local = IL.GetReferencedVariable(method, instruction);
                        return new ByReferenceType(local.VariableType);
                    case Code.Ldelema:
                        return new ByReferenceType((TypeReference)instruction.Operand);
                    case Code.Ldflda:
                        field = (FieldReference)instruction.Operand;
                        return new ByReferenceType(field.DeclaringType);
                    case Code.Ldsflda:
                        field = (FieldReference)instruction.Operand;
                        return new ByReferenceType(field.DeclaringType);
                    case Code.Sizeof:
                        return method.Module.TypeSystem.Int32;

                    // memory allocation
                    case Code.Localloc:
                        return method.Module.TypeSystem.IntPtr;

                    // metadata call
                    case Code.Ldtoken:
                        return method.Module.TypeSystem.IntPtr;

                    // exception handling
                    case Code.Leave:
                    case Code.Leave_S:
                        IsTryEndInstruction(method.Body, instruction, out var exceptionType);
                        return exceptionType ?? throw new NotSupportedException("Instruction is not a catch instruction.");

                    default:
                        throw new NotSupportedException($"Instruction {instruction.OpCode} not supported.");
                }
            }
            public abstract class ArgumentSource
            {
                public Instruction[] Instructions { get; set; } = [];
            }
            public class FlowPath<TSource>(TSource[] parametersSources) where TSource : ArgumentSource
            {
                public TSource[] ParametersSources { get; set; } = parametersSources;
            }
            public class StackTopTypePath(TypeReference? type, Instruction actual) : ArgumentSource, IEquatable<StackTopTypePath>
            {
                public TypeReference? StackTopType { get; } = type;
                /// <summary>
                /// <para> The instruction that pushed the value onto the stack. </para>
                /// <para> If the instruction is Dup, this is the instruction that dup copied the value.</para>
                /// <para>Otherwise, this is itself.</para>
                /// </summary>
                public Instruction RealPushValueInstruction => actual;

                public override string ToString() => $"[{StackTopType?.FullName}] (inst: {Instructions.Length}, hash: {GetHashCode()})";

                public override int GetHashCode() {
                    if (Instructions.Length == 0) {
                        return -1;
                    }
                    return HashCode.Combine(Instructions.First(), Instructions.Last());
                }
                public override bool Equals(object? obj) {
                    if (obj is StackTopTypePath other) {
                        return Instructions.First() == other.Instructions.First() && Instructions.Last() == other.Instructions.Last();
                    }
                    return false;
                }
                public bool Equals(StackTopTypePath? other) {
                    if (other is null) return false;
                    return Instructions.First() == other.Instructions.First() && Instructions.Last() == other.Instructions.Last();
                }
            }
            public class ParameterSource(ParameterDefinition parameter) : ArgumentSource
            {
                public ParameterDefinition Parameter { get; } = parameter;

                public override string ToString() => $"[{Parameter.Name}:{Parameter.ParameterType.Name}] (inst: {Instructions.Length})";
            }
            public class InstructionArgsSource(int index) : ArgumentSource
            {
                public int Index { get; } = index;

                public override string ToString() => $"[{Index}] (inst: {Instructions.Length})";
            }
            /// <summary>
            /// Analyzes Parameter sources considering control flow and multiple execution paths
            /// </summary>
            /// <param name="caller">Containing method</param>
            /// <param name="target">Method call/newobj instruction</param>
            /// <returns>Array of possible Parameter flow paths</returns>
            public static FlowPath<ParameterSource>[] AnalyzeParametersSources(MethodDefinition caller, Instruction target, Dictionary<Instruction, List<Instruction>>? cachedJumpSitess = null) {

                cachedJumpSitess ??= BuildJumpSitesMap(caller);

                // Resolve method signature
                MethodReference callee = (MethodReference)target.Operand;
                bool hasThis = (target.OpCode == OpCodes.Call || target.OpCode == OpCodes.Callvirt) && callee.HasThis;
                int paramCount = callee.Parameters.Count + (hasThis ? 1 : 0);
                if (target.OpCode == OpCodes.Newobj)
                    paramCount = callee.Parameters.Count;

                // Initialize analysis queue
                Stack<ReverseAnalysisContext> workStack = new Stack<ReverseAnalysisContext>();
                StackDemand initialDemand = new StackDemand(paramCount, GetPushCount(caller.Body, target));
                workStack.Push(new ReverseAnalysisContext(
                    current: target,
                    previous: target.Previous,
                    stackDemand: initialDemand,
                    path: new(),
                    isBranch: false,
                    visited: []
                ));

                List<FlowPath<ParameterSource>> paths = [];
                // var visited = new HashSet<(Instruction, int)>(); // Trace visited (offset, stackBalance)

                while (workStack.Count > 0) {
                    var ctx = workStack.Pop();

                    var stateKey = ctx.Current; // (ctx.Current, ctx.StackDemand.StackBalance);
                    var visited = ctx.Visited;
                    if (visited.Contains(stateKey)) continue;
                    visited.Add(stateKey);

                    // Clone context to prevent state pollution
                    var stackDemand = ctx.StackDemand.Clone();
                    ReversibleLinkedList<Instruction> path = new ReversibleLinkedList<Instruction>(ctx.Path);

                    // Process tail instruction's reverse stack effect
                    int originalPush = GetPushCount(caller.Body, ctx.Current);
                    int originalPop = GetPopCount(caller.Body, ctx.Current);

                    if (!stackDemand.ApplyInstruction(
                        pop: originalPush,
                        push: originalPop)) {
                        continue; // Invalid stack state
                    }
                    path.Add(ctx.Current);

                    // Termination condition
                    if (stackDemand.StackBalance == 0) {
                        paths.Add(ProcessCompletedPath(caller, path.ReverseToArray(), callee));
                        if (paths.Count > 256) {
                            workStack.Clear();
                        }
                        continue;
                    }

                    // Handle branching paths
                    if (cachedJumpSitess.TryGetValue(ctx.Current, out var jumpSitess)) {
                        foreach (var source in jumpSitess) {
                            var branchStack = stackDemand.Clone();
                            workStack.Push(new ReverseAnalysisContext(
                                current: source,
                                previous: source.Previous,
                                stackDemand: branchStack,
                                path: new(path),
                                isBranch: true,
                                visited: [.. ctx.Visited]
                            ));
                        }
                    }

                    // Linear backtracing
                    if (ctx.Previous != null) {
                        if (!IsTerminatorInstruction(ctx.Previous)) {
                            workStack.Push(new ReverseAnalysisContext(
                                current: ctx.Previous,
                                previous: ctx.Previous.Previous,
                                stackDemand: stackDemand,
                                path: path,
                                isBranch: false,
                                visited: ctx.Visited
                            ));
                        }
                        else if (stackDemand.StackBalance == -1 && IsTryEndInstruction(caller.Body, ctx.Previous, out _)) {
                            path.Add(ctx.Previous);
                            paths.Add(ProcessCompletedPath(caller, path.ReverseToArray(), callee));
                            continue;
                        }
                    }
                }

                return BuildFlowPaths(caller.Body, paths);
            }
            private static int GetInstructionArgCount(MethodBody body, Instruction instruction) {
                switch (instruction.OpCode.FlowControl) {
                    case FlowControl.Branch when instruction.OpCode == OpCodes.Switch:
                        return 1;  // switch instruction needs 1 operand (selector value)
                    case FlowControl.Cond_Branch:
                        return GetConditionalBranchArgCount(body, instruction);
                }

                return GetPopCount(body, instruction);
            }

            private static int GetConditionalBranchArgCount(MethodBody body, Instruction instruction) {
                // Special handling for conditional branch instructions
                return instruction.OpCode.Code switch {
                    Code.Brtrue or Code.Brtrue_S => 1,  // need 1 boolean
                    Code.Brfalse or Code.Brfalse_S => 1,
                    Code.Beq or Code.Beq_S => 2,        // need 2 comparison values
                    Code.Bge or Code.Bge_S => 2,
                    Code.Bge_Un or Code.Bge_Un_S => 2,
                    Code.Bgt or Code.Bgt_S => 2,
                    Code.Bgt_Un or Code.Bgt_Un_S => 2,
                    Code.Ble or Code.Ble_S => 2,
                    Code.Ble_Un or Code.Ble_Un_S => 2,
                    Code.Blt or Code.Blt_S => 2,
                    Code.Blt_Un or Code.Blt_Un_S => 2,
                    Code.Bne_Un or Code.Bne_Un_S => 2,
                    _ => GetPopCount(body, instruction)
                };
            }
            public static FlowPath<InstructionArgsSource>[] AnalyzeInstructionArgsSources(MethodDefinition caller, Instruction target, Dictionary<Instruction, List<Instruction>>? cachedJumpSitess = null) {

                cachedJumpSitess ??= BuildJumpSitesMap(caller);

                var argsCount = GetInstructionArgCount(caller.Body, target);

                // initialize analysis queue
                Stack<ReverseAnalysisContext> workStack = new Stack<ReverseAnalysisContext>();
                StackDemand initialDemand = new StackDemand(argsCount, GetPushCount(caller.Body, target));

                workStack.Push(new ReverseAnalysisContext(
                    current: target,
                    previous: target.Previous,
                    stackDemand: initialDemand,
                    path: new(),
                    isBranch: false,
                    visited: []
                ));

                List<FlowPath<InstructionArgsSource>> paths = [];

                while (workStack.Count > 0) {
                    var ctx = workStack.Pop();

                    // Check for visited state to prevent loops
                    var stateKey = ctx.Current; // (ctx.Current, ctx.StackDemand.StackBalance);
                    var visited = ctx.Visited;
                    if (visited.Contains(stateKey)) continue;
                    visited.Add(stateKey);

                    // Clone context to prevent polluting
                    var stackDemand = ctx.StackDemand.Clone();
                    ReversibleLinkedList<Instruction> path = new ReversibleLinkedList<Instruction>(ctx.Path);

                    // Process the reverse stack effect of the tail instruction
                    int originalPush = GetPushCount(caller.Body, ctx.Current);
                    int originalPop = GetPopCount(caller.Body, ctx.Current);

                    if (!stackDemand.ApplyInstruction(
                        pop: originalPush,
                        push: originalPop))
                        continue;

                    path.Add(ctx.Current);

                    // All parameters have been resolved
                    if (stackDemand.StackBalance == 0) {
                        paths.Add(ProcessCompletedInstructionPath(caller, path.ReverseToArray(), argsCount));
                        if (paths.Count > 256) {
                            workStack.Clear();
                        }
                        continue;
                    }

                    // Process branch paths
                    if (cachedJumpSitess.TryGetValue(ctx.Current, out var jumpSitess)) {
                        foreach (var source in jumpSitess) {
                            var branchStack = stackDemand.Clone();

                            workStack.Push(new ReverseAnalysisContext(
                                current: source,
                                previous: source.Previous,
                                stackDemand: branchStack,
                                path: new(path),
                                isBranch: true,
                                visited: [.. ctx.Visited]
                            ));
                        }
                    }

                    // Linear backtracing
                    if (ctx.Previous != null) {
                        if (!IsTerminatorInstruction(ctx.Previous)) {
                            workStack.Push(new ReverseAnalysisContext(
                                current: ctx.Previous,
                                previous: ctx.Previous.Previous,
                                stackDemand: stackDemand,
                                path: path,
                                isBranch: false,
                                visited: ctx.Visited
                            ));
                        }
                        else if (stackDemand.StackBalance == -1 && IsTryEndInstruction(caller.Body, ctx.Previous, out _)) {
                            path.Add(ctx.Previous);
                            paths.Add(ProcessCompletedInstructionPath(caller, path.ReverseToArray(), argsCount));
                            continue;
                        }
                    }
                }

                return BuildFlowPaths(caller.Body, paths);
            }
            public static Dictionary<Instruction, List<Instruction>> BuildJumpSitesMap(MethodDefinition method) {
                Dictionary<Instruction, List<Instruction>> jumpTargets = new Dictionary<Instruction, List<Instruction>>();
                foreach (var instruction in method.Body.Instructions) {
                    if (instruction.Operand is ILLabel label) {
                        var target = label.Target;
                        if (target is null) {
                            continue;
                        }
                        if (!jumpTargets.TryGetValue(target!, out var sources)) {
                            sources = [];
                            jumpTargets.Add(target, sources);
                        }
                        sources.Add(instruction);
                    }
                    else if (instruction.Operand is Instruction target) {
                        if (!jumpTargets.TryGetValue(target, out var sources)) {
                            sources = [];
                            jumpTargets.Add(target, sources);
                        }
                        sources.Add(instruction);
                    }
                    else if (instruction.Operand is Instruction[] targets) {
                        foreach (var t in targets) {
                            if (!jumpTargets.TryGetValue(t, out var sources)) {
                                sources = [];
                                jumpTargets.Add(t, sources);
                            }
                            sources.Add(instruction);
                        }
                    }
                }
                return jumpTargets;
            }
            private static FlowPath<ParameterSource> ProcessCompletedPath(MethodDefinition caller, Instruction[] path, MethodReference callee) {
                return new FlowPath<ParameterSource>([.. AnalyzeMethodCallPath(caller, callee, path.Last(), path)]);
            }
            private static FlowPath<InstructionArgsSource> ProcessCompletedInstructionPath(MethodDefinition caller, Instruction[] path, int argsCount) {
                return new FlowPath<InstructionArgsSource>(
                    [.. AnalyzeInstructionArgs(caller.Body, path.Last(), argsCount, path)]
                );
            }
            private static List<ParameterSource> AnalyzeMethodCallPath(MethodDefinition caller, MethodReference callee, Instruction target, Instruction[] path) {
                path = [.. path.Take(path.Length - 1)];

                var deltas = ComputeStackDeltas(caller.Body, path);

                bool hasThis = (target.OpCode == OpCodes.Call || target.OpCode == OpCodes.Callvirt) && callee.HasThis;
                int paramCount = callee.Parameters.Count + (hasThis ? 1 : 0);

                if (target.OpCode == OpCodes.Newobj)
                    paramCount = callee.Parameters.Count;

                List<ParameterSource> parameters = new List<ParameterSource>();
                int currentIndex = path.Length - 1;

                for (int i = 0; i < paramCount; i++) {
                    if (currentIndex < 0) break;

                    var (start, end) = FindArgRange(path, deltas, currentIndex);
                    if (start == -1) break;

                    List<Instruction> paramInstructions = new List<Instruction>();
                    for (int j = start; j <= end; j++)
                        paramInstructions.Add(path[j]);

                    ParameterDefinition parameter;
                    if (hasThis) {
                        if (i == paramCount - 1) {
                            parameter = new ParameterDefinition("this", ParameterAttributes.None, callee.DeclaringType);
                        }
                        else {
                            parameter = callee.Parameters[paramCount - 2 - i];
                        }
                    }
                    else {
                        parameter = callee.Parameters[paramCount - 1 - i];
                    }

                    parameters.Add(new ParameterSource(parameter) { Instructions = [.. paramInstructions] });
                    currentIndex = start - 1;
                }

                parameters.Reverse();
                return parameters;
            }
            private static List<InstructionArgsSource> AnalyzeInstructionArgs(MethodBody body, Instruction target, int argsCount, Instruction[] path) {
                path = [.. path.Take(path.Length - 1)];
                var deltas = ComputeStackDeltas(body, path);
                List<InstructionArgsSource> argsSources = new List<InstructionArgsSource>();

                int currentIndex = path.Length - 1;

                for (int i = 0; i < argsCount; i++) {
                    if (currentIndex < 0) break;

                    var (start, end) = FindArgRange(path, deltas, currentIndex);
                    if (start == -1) break;

                    List<Instruction> argInstructions = new List<Instruction>();
                    for (int j = start; j <= end; j++)
                        argInstructions.Add(path[j]);

                    argsSources.Add(new InstructionArgsSource(i) {
                        Instructions = argInstructions.ToArray()
                    });

                    currentIndex = start - 1;
                }

                argsSources.Reverse();
                return argsSources;
            }
            private static (int start, int end) FindArgRange(Instruction[] path, int[] deltas, int startIndex) {
                int accumulated = 0;
                int end = startIndex;
                int start = startIndex;

                while (start >= 0) {
                    accumulated += deltas[start];
                    if (accumulated == 1) break;
                    if (accumulated > 1) return (-1, -1);
                    start--;
                }

                return start >= 0 ? (start, end) : (-1, -1);
            }
            static bool IsStackEffectFree(MethodBody body, Instruction instruction) {
                switch (instruction.OpCode.Code) {
                    case Code.Br:
                    case Code.Br_S:
                    case Code.Nop:
                        return true;
                    case Code.Leave:
                    case Code.Leave_S:
                        foreach (var handler in body.ExceptionHandlers) {
                            if (handler.TryEnd.Previous == instruction) {
                                return false;
                            }
                        }
                        return true;
                    default:
                        return false;
                }
            }
            static bool IsTerminatorInstruction(Instruction instruction) {
                return instruction.OpCode.Code switch {
                    Code.Ret or Code.Throw or Code.Br or Code.Br_S or Code.Leave or Code.Leave_S => true,
                    _ => false,
                };
            }
            static bool IsTryEndInstruction(MethodBody body, Instruction instruction, [NotNullWhen(true)] out TypeReference? exceptionType) {
                if (instruction.OpCode == OpCodes.Leave || instruction.OpCode == OpCodes.Leave_S) {
                    foreach (var handler in body.ExceptionHandlers) {
                        if (handler.TryEnd.Previous == instruction) {
                            exceptionType = handler.CatchType;
                            return true;
                        }
                    }
                }

                exceptionType = null;
                return false;
            }
            private static FlowPath<TSource>[] BuildFlowPaths<TSource>(MethodBody body, List<FlowPath<TSource>> rawPaths) where TSource : ArgumentSource {
                foreach (var path in rawPaths) {
                    foreach (var paramSource in path.ParametersSources) {
                        paramSource.Instructions = [.. paramSource.Instructions.Where(inst => !IsStackEffectFree(body, inst))];
                    }
                }

                return [.. rawPaths];
            }
            public static int GetPopCount(MethodBody body, Instruction instruction) {
                if (instruction.OpCode == OpCodes.Ret && body.Method.ReturnType.FullName != body.Method.Module.TypeSystem.Void.FullName) {
                    return 1;
                }
                switch (instruction.OpCode.StackBehaviourPop) {
                    case StackBehaviour.Pop0:
                        return 0;
                    case StackBehaviour.Pop1:
                    case StackBehaviour.Popi:
                    case StackBehaviour.Popref:
                        return 1;
                    case StackBehaviour.Pop1_pop1:
                    case StackBehaviour.Popi_pop1:
                    case StackBehaviour.Popi_popi:
                    case StackBehaviour.Popi_popi8:
                    case StackBehaviour.Popi_popr4:
                    case StackBehaviour.Popi_popr8:
                    case StackBehaviour.Popref_pop1:
                    case StackBehaviour.Popref_popi:
                        return 2;
                    case StackBehaviour.Popi_popi_popi:
                    case StackBehaviour.Popref_popi_popr4:
                    case StackBehaviour.Popref_popi_popr8:
                    case StackBehaviour.Popref_popi_popi:
                    case StackBehaviour.Popref_popi_popi8:
                    case StackBehaviour.Popref_popi_popref:
                        return 3;
                    case StackBehaviour.PopAll:
                        return 0;
                    case StackBehaviour.Varpop:
                        if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Newobj) {
                            MethodReference methodRef = (MethodReference)instruction.Operand;
                            int count = methodRef.Parameters.Count;
                            if (instruction.OpCode != OpCodes.Newobj && methodRef.HasThis)
                                count++;
                            return count;
                        }
                        else if (instruction.OpCode == OpCodes.Calli) {
                            return ((CallSite)instruction.Operand).Parameters.Count + 1;
                        }
                        else if (instruction.OpCode == OpCodes.Ret) {
                            var method = instruction.Operand as MethodReference ?? instruction.Operand as MethodDefinition;
                            return method != null && method.ReturnType.FullName != "System.Void" ? 1 : 0;
                        }
                        return 0;
                    default:
                        return 0;
                }
            }
            public static int GetPushCount(MethodBody body, Instruction instruction) {

                if ((instruction.OpCode == OpCodes.Leave || instruction.OpCode == OpCodes.Leave_S) &&
                    body.ExceptionHandlers.Any(handler => handler.TryEnd.Previous == instruction)) {
                    return 1;
                }

                switch (instruction.OpCode.StackBehaviourPush) {
                    case StackBehaviour.Push0:
                        return 0;
                    case StackBehaviour.Push1:
                    case StackBehaviour.Pushi:
                    case StackBehaviour.Pushi8:
                    case StackBehaviour.Pushr4:
                    case StackBehaviour.Pushr8:
                    case StackBehaviour.Pushref:
                        return 1;
                    case StackBehaviour.Push1_push1:
                        return 2;
                    case StackBehaviour.Varpush:
                        if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) {
                            MethodReference method = (MethodReference)instruction.Operand;
                            return method.ReturnType.FullName == "System.Void" ? 0 : 1;
                        }
                        else if (instruction.OpCode == OpCodes.Calli) {
                            CallSite sig = (CallSite)instruction.Operand;
                            return sig.ReturnType.FullName == "System.Void" ? 0 : 1;
                        }
                        else if (instruction.OpCode == OpCodes.Newobj) {
                            return 1;
                        }
                        return 0;
                    default:
                        return 0;
                }
            }
            private static int[] ComputeStackDeltas(MethodBody body, Instruction[] path) {
                return [.. path.Select(p => GetStackDelta(body, p))];
            }
            private static int GetStackDelta(MethodBody body, Instruction instruction) {
                int pop = GetPopCount(body, instruction);
                int push = GetPushCount(body, instruction);
                return push - pop;
            }
            private class ReverseAnalysisContext(Instruction current, Instruction previous, StackDemand stackDemand, ReversibleLinkedList<Instruction> path, bool isBranch, HashSet<Instruction> visited)
            {
                public readonly Instruction Current = current;
                public readonly Instruction Previous = previous;
                public readonly StackDemand StackDemand = stackDemand;
                public readonly ReversibleLinkedList<Instruction> Path = path;
                public readonly bool IsBranch = isBranch;
                public readonly HashSet<Instruction> Visited = visited;
            }
            private class StackDemand
            {
                public int ParametersToResolve { get; private set; }
                public int PushCount { get; private set; }
                public int StackBalance { get; private set; }

                public StackDemand(int popCount, int pushCount) {
                    ParametersToResolve = popCount;
                    PushCount = pushCount;
                    StackBalance = -pushCount;
                }

                public bool ApplyInstruction(int push, int pop) {
                    StackBalance += pop - push;
                    return true;
                }

                public StackDemand Clone() {
                    return new StackDemand(ParametersToResolve, PushCount) {
                        StackBalance = StackBalance
                    };
                }
            }
        }
    }
}


namespace OTAPI.UnifiedServerProcess.Commons
{
    public static partial class MonoModCommon
    {
        public static class IL
        {
            /// <summary>
            /// The name of the "this" Parameter is an empty string rather than "this".
            /// </summary>
            public const string ThisParameterName = "";
            public static Instruction BuildParameterLoad(MethodDefinition method, MethodBody body, ParameterDefinition parameter) {
                if (method.HasThis && ((body is not null && body.ThisParameter == parameter) || parameter.Name == "")) {
                    return Instruction.Create(OpCodes.Ldarg_0);
                }
                else {
                    int index = method.Parameters.IndexOf(parameter) + (method.HasThis ? 1 : 0);
                    return index switch {
                        -1 => throw new ArgumentException("Parameter not found in method", "parameter"),
                        0 => Instruction.Create(OpCodes.Ldarg_0),
                        1 => Instruction.Create(OpCodes.Ldarg_1),
                        2 => Instruction.Create(OpCodes.Ldarg_2),
                        3 => Instruction.Create(OpCodes.Ldarg_3),
                        _ => Instruction.Create(index < byte.MaxValue ? OpCodes.Ldarg_S : OpCodes.Ldarg, parameter)
                    };
                }
            }
            public static Instruction BuildParameterSet(MethodDefinition method, MethodBody body, ParameterDefinition parameter) {
                if (method.HasThis && ((body is not null && body.ThisParameter == parameter) || parameter.Name == "")) {
                    throw new ArgumentException("Cannot set \"this\" TracingParameter", "parameter");
                }
                else {
                    int index = method.Parameters.IndexOf(parameter) + (method.HasThis ? 1 : 0);
                    if (index < 0) {
                        throw new ArgumentException("Parameter not found in method", "parameter");
                    }
                    return Instruction.Create(index < byte.MaxValue ? OpCodes.Starg_S : OpCodes.Starg, parameter);
                }
            }
            public static Instruction BuildParameterLoadAddress(MethodDefinition method, MethodBody body, ParameterDefinition parameter) {
                if (method.HasThis && ((body is not null && body.ThisParameter == parameter) || parameter.Name == "")) {
                    return Instruction.Create(OpCodes.Ldarga_S, body?.ThisParameter ?? throw new InvalidOperationException());
                }
                else {
                    int index = method.Parameters.IndexOf(parameter) + (method.HasThis ? 1 : 0);
                    if (index < 0) {
                        throw new ArgumentException("Parameter not found in method", "parameter");
                    }
                    return Instruction.Create(index < byte.MaxValue ? OpCodes.Ldarga_S : OpCodes.Ldarga, parameter);
                }
            }
            public static Instruction BuildVariableLoad(MethodDefinition method, MethodBody body, VariableDefinition variable) {
                int index = method.Body.Variables.IndexOf(variable);
                if (index < 0) {
                    throw new ArgumentException("Variable not found in method", "variable");
                }
                return index switch {
                    0 => Instruction.Create(OpCodes.Ldloc_0),
                    1 => Instruction.Create(OpCodes.Ldloc_1),
                    2 => Instruction.Create(OpCodes.Ldloc_2),
                    3 => Instruction.Create(OpCodes.Ldloc_3),
                    _ => Instruction.Create(index < byte.MaxValue ? OpCodes.Ldloc_S : OpCodes.Ldloc, variable)
                };
            }
            public static Instruction BuildVariableStore(MethodDefinition method, MethodBody body, VariableDefinition variable) {
                int index = method.Body.Variables.IndexOf(variable);
                if (index < 0) {
                    throw new ArgumentException("Variable not found in method", "variable");
                }
                return index switch {
                    0 => Instruction.Create(OpCodes.Stloc_0),
                    1 => Instruction.Create(OpCodes.Stloc_1),
                    2 => Instruction.Create(OpCodes.Stloc_2),
                    3 => Instruction.Create(OpCodes.Stloc_3),
                    _ => Instruction.Create(index < byte.MaxValue ? OpCodes.Stloc_S : OpCodes.Stloc, variable)
                };
            }
            public static Instruction BuildVariableLoadAddress(MethodDefinition method, MethodBody body, VariableDefinition variable) {
                int index = method.Body.Variables.IndexOf(variable);
                if (index < 0) {
                    throw new ArgumentException("Variable not found in method", "variable");
                }
                return Instruction.Create(index < byte.MaxValue ? OpCodes.Ldloca_S : OpCodes.Ldloca, variable);
            }
            public static ParameterDefinition GetReferencedParameter(MethodDefinition method, Instruction instruction) {
                ParameterDefinition? tmpCheck = null;
                int paramIndex = instruction.OpCode.Code switch {
                    Code.Ldarg_0 or
                    Code.Ldarg_1 or
                    Code.Ldarg_2 or
                    Code.Ldarg_3 => instruction.OpCode.Code - Code.Ldarg_0 - (method.HasThis ? 1 : 0),
                    Code.Ldarg_S or
                    Code.Ldarg or
                    Code.Ldarga_S or
                    Code.Ldarga or
                    Code.Starg_S or
                    Code.Starg => (tmpCheck = (ParameterDefinition)instruction.Operand).Index,
                    _ => throw new InvalidOperationException($"Unsupported opcode {instruction.OpCode.Code}")
                };
                if (paramIndex == -1) {
                    return method.Body?.ThisParameter ?? new ParameterDefinition("", ParameterAttributes.None, method.DeclaringType);
                }
                var param = method.Parameters[paramIndex];
                if (tmpCheck is not null && tmpCheck.Name != param.Name) {
                    throw new InvalidOperationException("Operand TracingParameter is invalid");
                }
                return param;
            }
            public static bool TryGetReferencedParameter(MethodDefinition method, Instruction instruction, [NotNullWhen(true)] out ParameterDefinition? parameter) {
                return TryGetReferencedParameter(method, instruction, out _, out parameter);
            }
            public static bool TryGetReferencedParameter(MethodDefinition method, Instruction instruction, out int paramInnerIndex, [NotNullWhen(true)] out ParameterDefinition? parameter) {
                ParameterDefinition? tmpCheck = null;

                paramInnerIndex = instruction.OpCode.Code switch {
                    Code.Ldarg_0 => 0,
                    Code.Ldarg_1 => 1,
                    Code.Ldarg_2 => 2,
                    Code.Ldarg_3 => 3,
                    Code.Ldarg_S or
                    Code.Ldarg or
                    Code.Ldarga_S or
                    Code.Ldarga or
                    Code.Starg_S or
                    Code.Starg => (tmpCheck = (ParameterDefinition)instruction.Operand).Index + (method.HasThis ? 1 : 0),
                    _ => -1
                };

                if (paramInnerIndex == -1) {
                    parameter = null;
                    return false;
                }

                if (paramInnerIndex == 0 && method.HasThis) {
                    parameter = method.Body?.ThisParameter ?? new ParameterDefinition("", ParameterAttributes.None, method.DeclaringType);
                }
                else {
                    int paramIndex = paramInnerIndex - (method.HasThis ? 1 : 0);
                    parameter = method.Parameters[paramIndex];
                    if (tmpCheck is not null && tmpCheck.Name != parameter.Name) {
                        throw new InvalidOperationException("Operand TracingParameter is invalid");
                    }
                }
                return true;
            }
            public static VariableDefinition GetReferencedVariable(MethodDefinition method, Instruction instruction) {
                VariableDefinition? tmpCheck = null;
                int localIndex = instruction.OpCode.Code switch {
                    Code.Ldloc_0 or Code.Stloc_0 => 0,
                    Code.Ldloc_1 or Code.Stloc_1 => 1,
                    Code.Ldloc_2 or Code.Stloc_2 => 2,
                    Code.Ldloc_3 or Code.Stloc_3 => 3,
                    Code.Ldloc_S or
                    Code.Ldloc or
                    Code.Ldloca or
                    Code.Ldloca_S or
                    Code.Stloc or
                    Code.Stloc_S or
                    Code.Stloc => (tmpCheck = (VariableDefinition)instruction.Operand).Index,
                    _ => throw new InvalidOperationException($"Unsupported opcode {instruction.OpCode.Code}")
                };

                if (tmpCheck is not null && tmpCheck != method.Body.Variables[localIndex]) {
                    throw new InvalidOperationException("Operand variable is invalid");
                }

                return method.Body.Variables[localIndex];
            }
            public static bool TryGetReferencedVariable(MethodDefinition method, Instruction instruction, [NotNullWhen(true)] out VariableDefinition? variable) {
                return TryGetReferencedVariable(method, instruction, out _, out variable);
            }
            public static bool TryGetReferencedVariable(MethodDefinition method, Instruction instruction, out int localIndex, [NotNullWhen(true)] out VariableDefinition? variable) {
                VariableDefinition? tmpCheck = null;

                localIndex = instruction.OpCode.Code switch {
                    Code.Ldloc_0 or Code.Stloc_0 => 0,
                    Code.Ldloc_1 or Code.Stloc_1 => 1,
                    Code.Ldloc_2 or Code.Stloc_2 => 2,
                    Code.Ldloc_3 or Code.Stloc_3 => 3,

                    Code.Ldloc_S or
                    Code.Ldloc or
                    Code.Ldloca_S or
                    Code.Ldloca or
                    Code.Stloc_S or
                    Code.Stloc => (tmpCheck = (VariableDefinition)instruction.Operand).Index,
                    _ => -1
                };

                if (tmpCheck is not null && tmpCheck != method.Body.Variables[localIndex]) {
                    throw new ArgumentException("Operand variable is invalid", "instruction");
                }

                if (localIndex == -1) {
                    variable = null;
                    return false;
                }

                variable = method.Body.Variables[localIndex];
                return true;
            }
            public static bool MatchSetVariable(MethodDefinition method, Instruction instruction, [NotNullWhen(true)] out VariableDefinition? variable) {
                VariableDefinition? tmpCheck = null;

                int localIndex = instruction.OpCode.Code switch {
                    Code.Stloc_0 => 0,
                    Code.Stloc_1 => 1,
                    Code.Stloc_2 => 2,
                    Code.Stloc_3 => 3,

                    Code.Stloc_S or
                    Code.Stloc => (tmpCheck = (VariableDefinition)instruction.Operand).Index,
                    _ => -1
                };

                if (tmpCheck is not null && tmpCheck != method.Body.Variables[localIndex]) {
                    throw new ArgumentException("Operand variable is invalid", "instruction");
                }

                if (localIndex == -1) {
                    variable = null;
                    return false;
                }

                variable = method.Body.Variables[localIndex];
                return true;
            }
            public static bool MatchLoadVariableAddress(MethodDefinition method, Instruction instruction, [NotNullWhen(true)] out VariableDefinition? variable) {
                VariableDefinition? tmpCheck = null;

                int localIndex = instruction.OpCode.Code switch {
                    Code.Ldloca_S or
                    Code.Ldloca => (tmpCheck = (VariableDefinition)instruction.Operand).Index,
                    _ => -1
                };

                if (tmpCheck is not null && tmpCheck != method.Body.Variables[localIndex]) {
                    throw new ArgumentException("Operand variable is invalid", "instruction");
                }

                if (localIndex == -1) {
                    variable = null;
                    return false;
                }

                variable = method.Body.Variables[localIndex];
                return true;
            }
            public static bool MatchLoadVariable(MethodDefinition method, Instruction instruction, [NotNullWhen(true)] out VariableDefinition? variable) {
                VariableDefinition? tmpCheck = null;

                int localIndex = instruction.OpCode.Code switch {
                    Code.Ldloc_0 => 0,
                    Code.Ldloc_1 => 1,
                    Code.Ldloc_2 => 2,
                    Code.Ldloc_3 => 3,

                    Code.Ldloc_S or
                    Code.Ldloc => (tmpCheck = (VariableDefinition)instruction.Operand).Index,
                    _ => -1
                };

                if (tmpCheck is not null && tmpCheck != method.Body.Variables[localIndex]) {
                    throw new ArgumentException("Operand variable is invalid", "instruction");
                }

                if (localIndex == -1) {
                    variable = null;
                    return false;
                }

                variable = method.Body.Variables[localIndex];
                return true;
            }
            public static TypeReference GetMethodReturnType(MethodReference target, MethodDefinition callerContext) {
                if (target is GenericInstanceMethod genericMethod) {
                    return ResolveGenericReturnType(genericMethod, genericMethod.ReturnType);
                }
                if (target.DeclaringType is GenericInstanceType declaringGenericType) {
                    return ResolveGenericParameterInType(declaringGenericType, target.ReturnType);
                }
                return target.ReturnType;
            }
            private static TypeReference ResolveGenericReturnType(GenericInstanceMethod genericMethod, TypeReference returnType) {
                if (returnType.IsGenericParameter) {
                    var genericParam = (GenericParameter)returnType;
                    if (genericParam.Owner is MethodReference) {
                        if (genericMethod.GenericArguments.Count > genericParam.Position) {
                            return genericMethod.GenericArguments[genericParam.Position];
                        }
                    }
                    else if (genericParam.Owner is TypeReference) {
                        if (genericMethod.DeclaringType is GenericInstanceType declaringGenericType) {
                            return ResolveGenericParameterInType(declaringGenericType, returnType);
                        }
                    }
                }

                // handle nested generic type (e.g. List<T> where T is a generic type)
                if (returnType is GenericInstanceType genericReturnType) {
                    var newGenericReturnType = new GenericInstanceType(genericReturnType.ElementType);
                    foreach (var arg in genericReturnType.GenericArguments) {
                        newGenericReturnType.GenericArguments.Add(ResolveGenericReturnType(genericMethod, arg));
                    }
                    return newGenericReturnType;
                }

                return returnType;
            }
            private static TypeReference ResolveGenericParameterInType(GenericInstanceType genericType, TypeReference type) {
                if (type.IsGenericParameter) {
                    var genericParam = (GenericParameter)type;
                    if (genericParam.Owner is TypeReference && genericType.GenericArguments.Count > genericParam.Position) {
                        return genericType.GenericArguments[genericParam.Position];
                    }
                }
                return type;
            }
            public static Instruction? GetBaseConstructorCall(MethodBody ctorBody) {
                if (ctorBody.Method.Name != ".ctor") {
                    throw new ArgumentException("Method is not a constructor", "ctorBody");
                }
                var ctor = ctorBody.Method;
                for (int i = 0; i < ctorBody.Instructions.Count; i++) {
                    var check = ctorBody.Instructions[i];
                    if (check is { OpCode.Code: Code.Call, Operand: MethodReference { Name: ".ctor" } checkCtor }) {
                        var checkCtorTypeDef = checkCtor.DeclaringType.Resolve();
                        if (checkCtorTypeDef.FullName == ctor.DeclaringType.BaseType.FullName || checkCtorTypeDef.FullName == ctor.DeclaringType.FullName) {
                            return check;
                        }
                    }
                }
                return null;
            }
        }
    }
}


namespace OTAPI.UnifiedServerProcess.Commons
{
    public static partial class MonoModCommon
    {
        public static class Structure
        {
            public static TypeReference CreateTypeReference(TypeDefinition type, ModuleDefinition module) {
                TypeReference result = new TypeReference(type.Namespace, type.Name, module, type.Scope) {
                    IsValueType = type.IsValueType,
                };
                if (result.HasGenericParameters) {
                    foreach (var param in type.GenericParameters) {
                        result.GenericParameters.Add(new GenericParameter(param.Name, result));
                    }
                }
                if (type.DeclaringType is not null) {
                    result.DeclaringType = CreateTypeReference(type.DeclaringType, module);
                }
                return result;
            }
            static string GenerateKeyForGenericParameter(GenericParameter param) {
                if (param.DeclaringMethod is not null) {
                    return param.DeclaringMethod.GetIdentifier() + ":" + param.Position;
                }
                else if (param.DeclaringType is not null) {
                    return param.DeclaringType.Resolve().FullName + ":" + param.Position;
                }
                else {
                    throw new InvalidOperationException("Unknown generic TracingParameter");
                }
            }
            public static MethodReference CreateMethodReference(MethodReference origiReference, MethodDefinition definition) {
                TypeReference declaringType = definition.DeclaringType;
                if (origiReference.DeclaringType is GenericInstanceType origiGenericType) {
                    GenericInstanceType genericType = new GenericInstanceType(declaringType);
                    foreach (var genArg in origiGenericType.GenericArguments) {
                        genericType.GenericArguments.Add(genArg);
                    }
                    declaringType = genericType;
                }
                MethodReference callee = new MethodReference(definition.Name, definition.ReturnType, declaringType) {
                    HasThis = definition.HasThis
                };
                foreach (var genParam in definition.GenericParameters) {
                    callee.GenericParameters.Add(genParam.Clone());
                }
                foreach (var param in definition.Parameters) {
                    callee.Parameters.Add(param.Clone());
                }
                if (origiReference is GenericInstanceMethod genericMethod) {
                    GenericInstanceMethod gerericCallee = new GenericInstanceMethod(callee);
                    foreach (var genArg in genericMethod.GenericArguments) {
                        gerericCallee.GenericArguments.Add(genArg);
                    }
                    callee = gerericCallee;
                }

                return callee;
            }
            public readonly struct MapOption
            {
                public MapOption() {
                    this.MethodReplaceMap = [];
                    this.TypeReplaceMap = [];
                    this.GenericProvider = [];
                    this.GenericParameterMap = [];
                }
                public MapOption(
                    Dictionary<TypeDefinition, TypeDefinition>? typeReplace = null,
                    Dictionary<MethodDefinition, MethodDefinition>? methodReplace = null,
                    Dictionary<IGenericParameterProvider, IGenericParameterProvider>? providers = null,
                    Dictionary<GenericParameter, TypeReference>? genericParameterMap = null) {
                    this.MethodReplaceMap = methodReplace ?? [];
                    this.TypeReplaceMap = typeReplace ?? [];
                    this.GenericProvider = providers ?? [];
                    this.GenericParameterMap = genericParameterMap?.ToDictionary(kv => GenerateKeyForGenericParameter(kv.Key), kv => kv.Value) ?? [];
                }
                public static MapOption Create(
                    (TypeDefinition from, TypeDefinition to)[]? replaceType = null,
                    (MethodDefinition from, MethodDefinition to)[]? replaceMethod = null,
                    (IGenericParameterProvider provideFrom, IGenericParameterProvider provideTo)[]? providers = null,
                    (GenericParameter paramFrom, TypeReference typeTo)[]? genericParameterMap = null) {
                    return new MapOption(
                        replaceType?.ToDictionary(x => x.from, x => x.to) ?? [],
                        replaceMethod?.ToDictionary(x => x.from, x => x.to) ?? [],
                        providers?.ToDictionary(x => x.provideFrom, x => x.provideTo) ?? [],
                        genericParameterMap?.ToDictionary(x => x.paramFrom, x => x.typeTo) ?? []);
                }
                public static MapOption CreateGenericProviderMap(
                    (IGenericParameterProvider provideFrom, IGenericParameterProvider provideTo)[]? providers = null) {
                    return new MapOption(
                        [],
                        [],
                        providers?.ToDictionary(x => x.provideFrom, x => x.provideTo) ?? []);
                }
                public readonly Dictionary<TypeDefinition, TypeDefinition> TypeReplaceMap;
                public readonly Dictionary<MethodDefinition, MethodDefinition> MethodReplaceMap;
                public readonly Dictionary<IGenericParameterProvider, IGenericParameterProvider> GenericProvider;
                public readonly Dictionary<string, TypeReference> GenericParameterMap;
            }
            public static GenericInstanceType DeepMapGenericInstanceType(GenericInstanceType instance, MapOption option) {
                var pattern = instance.ElementType;
                if (option.TypeReplaceMap.TryGetValue(pattern.Resolve(), out var mappedPattern)) {
                    pattern = mappedPattern;
                }

                GenericInstanceType result = new GenericInstanceType(pattern);
                foreach (var arg in instance.GenericArguments) {
                    if (arg is GenericInstanceType nestedGeneric) {
                        result.GenericArguments.Add(DeepMapGenericInstanceType(nestedGeneric, option));
                    }
                    else if (arg is GenericParameter genericParam) {
                        result.GenericArguments.Add(DeepMapGenericParameter(genericParam, option));
                    }
                    else if (arg is TypeReference typeRef) {
                        result.GenericArguments.Add(DeepMapTypeReference(typeRef, option));
                    }
                }
                return result;
            }
            public static TypeReference DeepMapGenericParameter(GenericParameter param, MapOption option) {
                if (option.GenericParameterMap.Count > 0 && option.GenericParameterMap.TryGetValue(GenerateKeyForGenericParameter(param), out var mappedParam)) {
                    return mappedParam;
                }
                GenericParameter copiedGenericParam;
                if (param.DeclaringMethod is not null) {
                    var elementMethodRef = param.DeclaringMethod.GetElementMethod();
                    var elementMethodDef = elementMethodRef.Resolve();

                    if (elementMethodDef is not null && option.MethodReplaceMap.TryGetValue(elementMethodDef, out var methodReplace)) {
                        elementMethodRef = methodReplace;
                    }
                    else if (option.GenericProvider.TryGetValue(elementMethodRef, out var genericProvider) && genericProvider is MethodReference genericProviderMethod) {
                        elementMethodRef = genericProviderMethod;
                    }
                    copiedGenericParam = elementMethodRef.GenericParameters[param.Position];
                }
                else if (param.DeclaringType is not null) {
                    var elementTypeRef = param.DeclaringType.GetElementType();
                    var elementTypeDef = elementTypeRef.Resolve();
                    if (elementTypeDef is not null && option.TypeReplaceMap.TryGetValue(elementTypeDef, out var typeReplace)) {
                        elementTypeRef = typeReplace;
                    }
                    else if (option.GenericProvider.TryGetValue(elementTypeRef, out var genericProvider) && genericProvider is TypeReference genericProviderType) {
                        elementTypeRef = genericProviderType;
                    }
                    copiedGenericParam = elementTypeRef.GenericParameters[param.Position];
                }
                else {
                    throw new InvalidOperationException("Unknown generic TracingParameter");
                }
                return copiedGenericParam;
            }
            public static GenericInstanceMethod DeepMapGenericInstanceMethod(GenericInstanceMethod instance, MapOption option) {
                var pattern = instance.ElementMethod;
                if (option.MethodReplaceMap.TryGetValue(pattern.Resolve(), out var mappedPattern)) {
                    pattern = mappedPattern;
                }

                GenericInstanceMethod result = new GenericInstanceMethod(pattern);
                foreach (var arg in instance.GenericArguments) {
                    if (arg is GenericInstanceType nestedGeneric) {
                        result.GenericArguments.Add(DeepMapGenericInstanceType(nestedGeneric, option));
                    }
                    else if (arg is GenericParameter genericParam) {
                        result.GenericArguments.Add(DeepMapGenericParameter(genericParam, option));
                    }
                    else if (arg is TypeReference typeRef) {
                        result.GenericArguments.Add(DeepMapTypeReference(typeRef, option));
                    }
                }
                return result;
            }
            public static TypeReference DeepMapTypeReference(TypeReference type, MapOption option) {
                if (type is GenericInstanceType generic) {
                    return DeepMapGenericInstanceType(generic, option);
                }
                else if (type is GenericParameter genericParam) {
                    return DeepMapGenericParameter(genericParam, option);
                }
                else if (type is ArrayType array) {
                    return new ArrayType(DeepMapTypeReference(array.ElementType, option), array.Rank);
                }
                else if (type is ByReferenceType byRef) {
                    return new ByReferenceType(DeepMapTypeReference(byRef.ElementType, option));
                }
                else if (type is PointerType ptr) {
                    return new PointerType(DeepMapTypeReference(ptr.ElementType, option));
                }
                else if (type is FunctionPointerType fnptr) {
                    return new PointerType(DeepMapTypeReference(fnptr.ElementType, option));
                }
                else if (type is RequiredModifierType required) {
                    return new RequiredModifierType(required.ModifierType, DeepMapTypeReference(required.ElementType, option));
                }
                var typeDef = type.Resolve();
                if (typeDef is not null && option.TypeReplaceMap.TryGetValue(typeDef, out var mappedTypeDef)) {
                    if (mappedTypeDef.Module != type.Module) {
                        return MonoModCommon.Structure.CreateTypeReference(mappedTypeDef, type.Module);
                    }
                    return mappedTypeDef;
                }
                else {
                    return type;
                }
            }
            public static MethodReference DeepMapMethodReference(MethodReference method, MapOption option) {
                var def = method.TryResolve();
                if (def is null && method.DeclaringType is ArrayType arrayType) {
                    arrayType = new ArrayType(DeepMapTypeReference(arrayType.ElementType, option), arrayType.Rank);
                    MethodReference mref = new MethodReference(method.Name, DeepMapTypeReference(method.ReturnType, option), arrayType) {
                        HasThis = method.HasThis
                    };
                    mref.Parameters.AddRange(method.Parameters.Select(p => new ParameterDefinition(DeepMapTypeReference(p.ParameterType, option))));
                    return mref;
                }
                else {
                    if (method is GenericInstanceMethod genericInstanceMethod) {
                        return DeepMapGenericInstanceMethod(genericInstanceMethod, option);
                    }
                    var declaringType = method.DeclaringType;
                    if (option.TypeReplaceMap.TryGetValue(declaringType.Resolve(), out var mappedDeclaringType)
                        && mappedDeclaringType.Methods.Any(m => m.GetIdentifier(false) == method.GetIdentifier(false)
                        && m.HasThis == method.HasThis)) {
                        declaringType = DeepMapTypeReference(method.DeclaringType, option);
                    }
                    MethodReference mref = new MethodReference(method.Name,
                        DeepMapTypeReference(method.ReturnType, option),
                        declaringType) {
                        HasThis = method.HasThis
                    };
                    mref.Parameters.AddRange(method.Parameters.Select(p => new ParameterDefinition(DeepMapTypeReference(p.ParameterType, option))));

                    return mref;
                }
            }
            /// <summary>
            /// Maps a method definition to a new method definition through the given type maps, and optionally maps its body
            /// <para>BE CAREFUL: This method won't add self to any potential declaring type</para>
            /// </summary>
            /// <param name="method"></param>
            /// <param name="shouldMapBody">Sometimes the methods will reference each other. so we can delay mapping the body until declarations of all methods are mapped</param>
            /// <returns></returns>
            public static MethodDefinition DeepMapMethodDef(MethodDefinition method, MapOption option, bool shouldMapBody) {

                if (option.MethodReplaceMap.TryGetValue(method, out var mappedMethod)) {
                    return mappedMethod;
                }
                MethodDefinition result = new MethodDefinition(method.Name, method.Attributes, method.Module.TypeSystem.Void);

                result.CustomAttributes.AddRange(method.CustomAttributes.Select(c => c.Clone()));

                foreach (var genParam in method.GenericParameters) {
                    result.GenericParameters.Add(genParam.Clone());
                }

                option.MethodReplaceMap.Add(method, result);

                for (int i = 0; i < method.GenericParameters.Count; i++) {
                    var from = method.GenericParameters[i];
                    var to = result.GenericParameters[i];

                    to.Constraints.Clear();
                    to.Constraints.AddRange(from.Constraints.Select(c => new GenericParameterConstraint(DeepMapTypeReference(c.ConstraintType, option))));
                }

                result.Name = method.Name;
                result.Attributes = method.Attributes;
                result.ReturnType = DeepMapTypeReference(method.ReturnType, option);

                foreach (var param in method.Parameters) {
                    var clonedParam = param.Clone();
                    clonedParam.ParameterType = DeepMapTypeReference(param.ParameterType, option);
                    result.Parameters.Add(clonedParam);
                }

                result.MetadataToken = result.MetadataToken;
                result.Attributes = method.Attributes;
                result.HasThis = method.HasThis;
                result.ImplAttributes = method.ImplAttributes;
                result.PInvokeInfo = method.PInvokeInfo;
                result.IsPreserveSig = method.IsPreserveSig;
                result.IsPInvokeImpl = method.IsPInvokeImpl;

                foreach (var @override in method.Overrides) {
                    // TODO: implement
                    result.Overrides.Add(@override);
                }

                if (shouldMapBody) {
                    result.Body = DeepMapMethodBody(method, result, option);
                }

                return result;
            }
            public static MethodBody? DeepMapMethodBody(MethodDefinition from, MethodDefinition to, MapOption option) {
                if (from.Body is null) {
                    return null;
                }

                MethodBody copied = new(to);
                var copyFrom = from.Body;

                copied.MaxStackSize = copyFrom.MaxStackSize;
                copied.InitLocals = copyFrom.InitLocals;
                copied.LocalVarToken = copyFrom.LocalVarToken;

                Dictionary<Instruction, Instruction> instMap = [];
                Dictionary<VariableDefinition, VariableDefinition> varMap = [];

                foreach (var local in copyFrom.Variables) {
                    VariableDefinition addLocal = new VariableDefinition(DeepMapTypeReference(local.VariableType, option));
                    copied.Variables.Add(addLocal);
                    varMap.Add(local, addLocal);
                }

                foreach (var inst in copyFrom.Instructions) {
                    Instruction addInst = Instruction.Create(OpCodes.Nop);
                    addInst.OpCode = inst.OpCode;
                    addInst.Operand = inst.Operand;
                    addInst.Offset = inst.Offset;

                    copied.Instructions.Add(addInst);
                    instMap.Add(inst, addInst);
                }

                foreach (var inst in copied.Instructions) {
                    int foundIndex;
                    if (inst.Operand is Instruction target) {
                        inst.Operand = instMap[target];
                    }
                    else if (inst.Operand is Instruction[] targets) {
                        Instruction[] newTargets = new Instruction[targets.Length];
                        for (int i = 0; i < targets.Length; i++) {
                            newTargets[i] = instMap[targets[i]];
                        }
                        inst.Operand = newTargets;
                    }
                    else if (inst.Operand is TypeReference typeRef) {
                        inst.Operand = DeepMapTypeReference(typeRef, option);
                    }
                    else if (inst.Operand is MethodReference methodRef) {
                        inst.Operand = DeepMapMethodReference(methodRef, option);
                    }
                    else if (inst.Operand is FieldReference fieldRef) {
                        var fieldDef = fieldRef.Resolve();
                        if (fieldDef is null) {
                            continue;
                        }
                        var declaringType = fieldRef.DeclaringType;
                        if (option.TypeReplaceMap.TryGetValue(declaringType.Resolve(), out var mappedDeclaringType)
                            && mappedDeclaringType.Fields.Any(f => f.Name == fieldDef.Name && f.IsStatic == fieldDef.IsStatic)) {
                            declaringType = DeepMapTypeReference(fieldRef.DeclaringType, option);
                        }
                        inst.Operand = new FieldReference(fieldRef.Name,
                            DeepMapTypeReference(fieldRef.FieldType, option),
                            declaringType);
                    }
                    else if (inst.Operand is GenericParameter genParam && (foundIndex = from.GenericParameters.IndexOf(genParam)) != -1) {
                        inst.Operand = copied.Method.GenericParameters[foundIndex];
                    }
                    else if (inst.Operand is ParameterDefinition param && (foundIndex = from.Parameters.IndexOf(param)) != -1) {
                        inst.Operand = copied.Method.Parameters[foundIndex];
                    }
                    else if (inst.Operand is VariableDefinition vardef) {
                        inst.Operand = varMap[vardef];
                    }
                }

                copied.ExceptionHandlers.AddRange(copyFrom.ExceptionHandlers.Select(o => {
                    ExceptionHandler c = new ExceptionHandler(o.HandlerType);
                    c.TryStart = o.TryStart is null ? null : copied.Instructions[copyFrom.Instructions.IndexOf(o.TryStart)];
                    c.TryEnd = o.TryEnd is null ? null : copied.Instructions[copyFrom.Instructions.IndexOf(o.TryEnd)];
                    c.FilterStart = o.FilterStart is null ? null : copied.Instructions[copyFrom.Instructions.IndexOf(o.FilterStart)];
                    c.HandlerStart = o.HandlerStart is null ? null : copied.Instructions[copyFrom.Instructions.IndexOf(o.HandlerStart)];
                    c.HandlerEnd = o.HandlerEnd is null ? null : copied.Instructions[copyFrom.Instructions.IndexOf(o.HandlerEnd)];
                    c.CatchType = o.CatchType;
                    return c;
                }));

                Instruction ResolveInstrOff(int off) {
                    // Can't check cloned instruction offsets directly, as those can change for some reason
                    for (var i = 0; i < copyFrom.Instructions.Count; i++)
                        if (copyFrom.Instructions[i].Offset == off)
                            return copied.Instructions[i];
                    throw new ArgumentException($"Invalid instruction offset {off}");
                }

                copied.Method.CustomDebugInformations.AddRange(copyFrom.Method.CustomDebugInformations.Select(o => {
                    if (o is AsyncMethodBodyDebugInformation ao) {
                        AsyncMethodBodyDebugInformation c = new AsyncMethodBodyDebugInformation();
                        if (ao.CatchHandler.Offset >= 0)
                            c.CatchHandler = ao.CatchHandler.IsEndOfMethod ? new InstructionOffset() : new InstructionOffset(ResolveInstrOff(ao.CatchHandler.Offset));
                        c.Yields.AddRange(ao.Yields.Select(off => off.IsEndOfMethod ? new InstructionOffset() : new InstructionOffset(ResolveInstrOff(off.Offset))));
                        c.Resumes.AddRange(ao.Resumes.Select(off => off.IsEndOfMethod ? new InstructionOffset() : new InstructionOffset(ResolveInstrOff(off.Offset))));
                        c.ResumeMethods.AddRange(ao.ResumeMethods);
                        return c;
                    }
                    else if (o is StateMachineScopeDebugInformation so) {
                        StateMachineScopeDebugInformation c = new StateMachineScopeDebugInformation();
                        c.Scopes.AddRange(so.Scopes.Select(s => new StateMachineScope(ResolveInstrOff(s.Start.Offset), s.End.IsEndOfMethod ? null : ResolveInstrOff(s.End.Offset))));
                        return c;
                    }
                    else
                        return o;
                }));

                copied.Method.DebugInformation.SequencePoints.AddRange(copyFrom.Method.DebugInformation.SequencePoints.Select(o => {
                    SequencePoint c = new SequencePoint(ResolveInstrOff(o.Offset), o.Document);
                    c.StartLine = o.StartLine;
                    c.StartColumn = o.StartColumn;
                    c.EndLine = o.EndLine;
                    c.EndColumn = o.EndColumn;
                    return c;
                }));

                return copied;
            }
            public static TypeDefinition MemberClonedType(TypeDefinition type, string newName, Dictionary<TypeDefinition, TypeDefinition>? mappedTypes = null, Dictionary<MethodDefinition, MethodDefinition>? mappedMethods = null) {
                mappedTypes ??= [];
                mappedMethods ??= [];
                Dictionary<TypeDefinition, TypeDefinition> inputTypes = mappedTypes.ToDictionary();
                MapOption mapCondition = new MonoModCommon.Structure.MapOption(mappedTypes, mappedMethods, [], []);

                static TypeDefinition ClonedType(TypeDefinition type, string newName, Dictionary<TypeDefinition, TypeDefinition> mappedTypes) {

                    TypeDefinition copied = new TypeDefinition(type.Namespace, newName, type.Attributes, type.BaseType);
                    mappedTypes.Add(type, copied);

                    foreach (var nested in type.NestedTypes) {
                        ClonedType(nested, nested.Name, mappedTypes);
                    }

                    copied.GenericParameters.AddRange(type.GenericParameters.Select(p => p.Clone()));
                    copied.CustomAttributes.AddRange(type.CustomAttributes);

                    if (type.DeclaringType is not null) {
                        var declaringType = type.DeclaringType;
                        if (mappedTypes.TryGetValue(declaringType, out var copiedDeclaringType)) {
                            declaringType = copiedDeclaringType;
                        }
                        declaringType.NestedTypes.Add(copied);
                    }
                    else {
                        type.Module.Types.Add(copied);
                    }

                    return copied;
                }
                static void ClonedMember(TypeDefinition from,
                    MonoModCommon.Structure.MapOption mapContext) {
                    var copied = mapContext.TypeReplaceMap[from];

                    foreach (var interfaceImpl in from.Interfaces) {
                        copied.Interfaces.Add(new InterfaceImplementation(MonoModCommon.Structure.DeepMapTypeReference(interfaceImpl.InterfaceType, mapContext)));
                    }

                    foreach (var field in from.Fields) {
                        FieldDefinition copiedField = new FieldDefinition(field.Name, field.Attributes, MonoModCommon.Structure.DeepMapTypeReference(field.FieldType, mapContext));
                        copied.Fields.Add(copiedField);
                    }

                    foreach (var method in from.Methods) {
                        var copiedMethod = MonoModCommon.Structure.DeepMapMethodDef(method, mapContext, false);
                        copied.Methods.Add(copiedMethod);
                    }

                    foreach (var property in from.Properties) {
                        copied.Properties.Add(new PropertyDefinition(property.Name, property.Attributes, property.PropertyType) {
                            GetMethod = property.GetMethod is null ? null : mapContext.MethodReplaceMap[property.GetMethod],
                            SetMethod = property.SetMethod is null ? null : mapContext.MethodReplaceMap[property.SetMethod]
                        });
                    }

                    foreach (var _event in from.Events) {
                        copied.Events.Add(new EventDefinition(_event.Name, _event.Attributes, _event.EventType) {
                            AddMethod = _event.AddMethod is null ? null : mapContext.MethodReplaceMap[_event.AddMethod],
                            RemoveMethod = _event.RemoveMethod is null ? null : mapContext.MethodReplaceMap[_event.RemoveMethod]
                        });
                    }
                }

                var copied = ClonedType(type, newName, mappedTypes);
                foreach (var to in mappedTypes.Keys) {
                    foreach (var gp in to.GenericParameters) {
                        for (int i = 0; i < gp.Constraints.Count; i++) {
                            var constraint = gp.Constraints[i];
                            var copiedConstraint = MonoModCommon.Structure.DeepMapTypeReference(constraint.ConstraintType, mapCondition);
                            gp.Constraints[i] = new GenericParameterConstraint(copiedConstraint);
                        }
                    }
                }

                foreach (var from in mappedTypes.Keys) {
                    if (inputTypes.ContainsKey(from)) {
                        continue;
                    }
                    ClonedMember(from, mapCondition);
                }

                foreach (var kv in mappedMethods) {
                    kv.Value.Body = MonoModCommon.Structure.DeepMapMethodBody(kv.Key, kv.Value, mapCondition);
                }

                return copied;
            }

            /// <summary>
            /// Creates an analysis-only instantiated <see cref="MethodReference"/> by substituting a single layer of generic parameters
            /// from <paramref name="impl"/> (method MVAR or declaring-type VAR) with their concrete generic arguments.
            /// </summary>
            /// <remarks>
            /// If <paramref name="impl"/> is a <see cref="GenericInstanceMethod"/>, binds the method generic parameters (MVAR);
            /// otherwise, if its declaring type is a <see cref="GenericInstanceType"/>, binds the declaring type generic parameters (VAR).
            /// The result is a flattened view for analysis and must not be emitted into IL/metadata.
            /// </remarks>
            /// <param name="impl">The method reference that may carry generic instantiation.</param>
            /// <returns>
            /// An instantiated method reference with no exposed parameters for the bound layer; or <paramref name="impl"/> if not applicable.
            /// </returns>
            public static MethodReference CreateInstantiatedMethod(MethodReference impl) {
                Dictionary<GenericParameter, TypeReference> map = [];

                MethodReference patten;

                if (impl is GenericInstanceMethod genericInstanceMethod) {
                    patten = genericInstanceMethod.ElementMethod;
                    for (var i = 0; i < genericInstanceMethod.GenericArguments.Count; i++) {
                        map.Add(patten.GenericParameters[i], genericInstanceMethod.GenericArguments[i]);
                    }
                }
                else if (impl.DeclaringType is GenericInstanceType genericInstanceType) {
                    var elementType = genericInstanceType.ElementType;
                    patten = impl;
                    for (var i = 0; i < genericInstanceType.GenericArguments.Count; i++) {
                        map.Add(elementType.GenericParameters[i], genericInstanceType.GenericArguments[i]);
                    }
                }
                else {
                    return impl;
                }

                MapOption option = new MapOption(genericParameterMap: map);
                return DeepMapMethodReference(patten, option);
            }
            /// <summary>
            /// Creates an analysis-only <see cref="MethodReference"/> by instantiating <paramref name="methodDef"/>
            /// with the generic arguments of a constructed <paramref name="declaringType"/>.
            /// </summary>
            /// <remarks>
            /// Only binds the declaring type's generic parameters (VAR). This is a flattened view for analysis and must not be emitted
            /// into IL/metadata, as it may not represent a valid metadata construct.
            /// </remarks>
            /// <param name="methodDef">The method definition declared on the generic type.</param>
            /// <param name="declaringType">The constructed declaring type (typically <see cref="GenericInstanceType"/>).</param>
            /// <returns>The instantiated method reference; or <paramref name="methodDef"/> if no instantiation is applicable.</returns>
            public static MethodReference CreateInstantiatedMethod(MethodDefinition methodDef, TypeReference declaringType) {
                Dictionary<GenericParameter, TypeReference> map = [];

                if (declaringType is GenericInstanceType genericInstanceType) {
                    var elementType = genericInstanceType.ElementType;
                    for (var i = 0; i < genericInstanceType.GenericArguments.Count; i++) {
                        map.Add(elementType.GenericParameters[i], genericInstanceType.GenericArguments[i]);
                    }
                }
                else {
                    return methodDef;
                }

                MapOption option = new MapOption(genericParameterMap: map);
                return DeepMapMethodReference(methodDef, option);
            }
        }
    }
}