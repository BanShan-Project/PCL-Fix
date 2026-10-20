using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    const string LatestHash = "6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386";
    const string OldHash = "5582952ded3b443503f66c23b810802bd20f1a3e847b54d9e09653a825859a36";
    static ModuleDefinition target = null!;
    static List<MethodDefinition> methods = null!;
    static readonly List<(MethodDefinition Method, string Description, string OriginalToken)> changes = new();
    static readonly HashSet<MethodDefinition> changed = new();
    static readonly Dictionary<string, MemberReference> members = new();
    static IEnumerable<TypeDefinition> Types(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Types));
    static string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
    static MethodDefinition Method(uint token) => (MethodDefinition)target.LookupToken(new MetadataToken(token));
    static FieldDefinition Field(string type, string name) => target.Types.SelectMany(Types).Single(t => t.FullName == type).Fields.Single(f => f.Name == name);
    static Instruction At(MethodDefinition m, int offset) => m.Body.Instructions.Single(i => i.Offset == offset);
    static MethodReference Ref(string declaringType, string name, int? argc = null) => members.Values.OfType<MethodReference>()
        .First(m => m.DeclaringType.FullName == declaringType && m.Name == name && (!argc.HasValue || m.Parameters.Count == argc));
    static Instruction I(OpCode op) => Instruction.Create(op);
    static Instruction I(OpCode op, MethodReference x) => Instruction.Create(op, x);
    static Instruction I(OpCode op, FieldReference x) => Instruction.Create(op, x);
    static Instruction I(OpCode op, string x) => Instruction.Create(op, x);
    static Instruction I(OpCode op, Instruction x) => Instruction.Create(op, x);
    static Instruction I(OpCode op, VariableDefinition x) => Instruction.Create(op, x);
    static void Mark(MethodDefinition m, string description)
    {
        changed.Add(m);
        changes.Add((m, description, $"0x{m.MetadataToken.ToUInt32():x8}"));
    }
    static void Nop(Instruction i) { i.OpCode = OpCodes.Nop; i.Operand = null; }
    static void InsertBefore(MethodDefinition m, Instruction anchor, IEnumerable<Instruction> ins)
    {
        var il = m.Body.GetILProcessor();
        foreach (var i in ins) il.InsertBefore(anchor, i);
    }
    static void InsertAfter(MethodDefinition m, Instruction anchor, IEnumerable<Instruction> ins)
    {
        var il = m.Body.GetILProcessor();
        foreach (var i in ins) { il.InsertAfter(anchor, i); anchor = i; }
    }
    static bool IsSelf(TypeReference t)
    {
        while (t is TypeSpecification s) t = s.ElementType;
        return t.Scope is ModuleDefinition md && md.Assembly.Name.Name == target.Assembly.Name.Name
            || t.Scope is AssemblyNameReference ar && ar.Name == target.Assembly.Name.Name;
    }
    static TypeReference MapType(TypeReference t)
    {
        if (members.TryGetValue(t.FullName, out var found) && found is TypeReference tr) return tr;
        if (t is GenericInstanceType g)
        {
            var r = new GenericInstanceType(MapType(g.ElementType));
            foreach (var arg in g.GenericArguments) r.GenericArguments.Add(MapType(arg));
            return r;
        }
        if (t is ArrayType a) return new ArrayType(MapType(a.ElementType), a.Rank);
        if (t is ByReferenceType b) return new ByReferenceType(MapType(b.ElementType));
        if (IsSelf(t)) throw new InvalidOperationException("Missing local type: " + t.FullName);
        return target.ImportReference(t);
    }
    static MemberReference MapMember(MemberReference r)
    {
        if (members.TryGetValue(r.FullName, out var found)) return found;
        if (r is TypeReference t) return MapType(t);
        if (IsSelf(r.DeclaringType)) throw new InvalidOperationException("Missing local member: " + r.FullName);
        return r switch {
            MethodReference m => target.ImportReference(m),
            FieldReference f => target.ImportReference(f),
            _ => throw new NotSupportedException(r.FullName)
        };
    }
    static void CloneNoArgumentMethod(MethodDefinition donor, MethodDefinition dst)
    {
        var body = new MethodBody(dst) { InitLocals = donor.Body.InitLocals, MaxStackSize = donor.Body.MaxStackSize };
        var locals = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var v in donor.Body.Variables) { var n = new VariableDefinition(MapType(v.VariableType)); locals[v] = n; body.Variables.Add(n); }
        var ins = donor.Body.Instructions.ToDictionary(i => i, i => I(OpCodes.Nop));
        foreach (var old in donor.Body.Instructions)
        {
            var n = ins[old]; n.OpCode = old.OpCode;
            n.Operand = old.Operand switch {
                null => null,
                Instruction jump => ins[jump],
                Instruction[] jumps => jumps.Select(j => ins[j]).ToArray(),
                VariableDefinition v => locals[v],
                MemberReference r => MapMember(r),
                ParameterDefinition => throw new InvalidOperationException("Unexpected donor parameter"),
                _ => old.Operand
            };
            body.Instructions.Add(n);
        }
        if (donor.Body.ExceptionHandlers.Count != 0) throw new InvalidOperationException("Unexpected donor EH");
        dst.Body = body;
        Mark(dst, "Restore file selection and asynchronous local modpack import using old body with new-module member mapping");
    }
    static void RestoreDownloads(ModuleDefinition old)
    {
        var loader = Method(0x06000513);
        var begin = At(loader, 0x1a); var end = At(loader, 0x4a);
        if (begin.Operand as string != "关闭下载") throw new InvalidOperationException("Unexpected LoaderInit body");
        foreach (var x in loader.Body.Instructions.SkipWhile(i => i != begin).TakeWhile(i => i != end).ToArray()) Nop(x);
        begin.OpCode = OpCodes.Ldsfld; begin.Operand = Field("PCL.ModDownload", "DlClientListLoader");
        Mark(loader, "Restore normal Minecraft version-list loader while preserving 2.99 ControlInit and completion callback");

        var donor = old.Types.Single(t => t.FullName == "PCL.ModModpack").Methods.Single(m => m.Name == "ModpackInstall" && m.Parameters.Count == 0);
        if (!donor.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "SelectFile")) throw new InvalidOperationException("Donor file selection is missing");
        CloneNoArgumentMethod(donor, Method(0x060001ef));

        var pack = Method(0x060001f0);
        var anchor = At(pack, 0x2da);
        var finallyStart = At(pack, 0x2e5);
        if (!(anchor.Operand as string ?? "").Contains("法律法规")) throw new InvalidOperationException("Unexpected modpack gate");
        foreach (var x in pack.Body.Instructions.SkipWhile(i => i != anchor).TakeWhile(i => i != finallyStart).ToArray()) Nop(x);
        var result = new VariableDefinition(pack.ReturnType); pack.Body.Variables.Add(result);
        var returnStart = I(OpCodes.Ldloc, result);
        var returnEnd = I(OpCodes.Ret);
        // EH end=null previously meant the end of the throwing method. Keep the new return outside every handler.
        foreach (var eh in pack.Body.ExceptionHandlers)
        {
            if (eh.HandlerEnd == null) eh.HandlerEnd = returnStart;
            if (eh.TryEnd == null) eh.TryEnd = returnStart;
        }
        pack.Body.Instructions.Add(returnStart); pack.Body.Instructions.Add(returnEnd);
        var dispatch = new List<Instruction>();
        var cases = new Dictionary<int, (string Name, uint Token, int Args)> {
            [0] = ("CurseForge",0x060001f4,5), [1] = ("HMCL",0x060001f6,3),
            [2] = ("MMC",0x060001f7,3), [3] = ("MCBBS",0x060001f8,4),
            [4] = ("Modrinth",0x060001f5,5), [9] = ("带启动器的压缩包",0x060001f9,3),
            [-1] = ("未能识别，假定为压缩包",0x060001fa,2)
        };
        var labels = cases.Keys.ToDictionary(k => k, k => I(OpCodes.Nop));
        dispatch.Add(I(OpCodes.Ldloc, pack.Body.Variables[4]));
        dispatch.Add(Instruction.Create(OpCodes.Switch, Enumerable.Range(0,10).Select(k => labels.ContainsKey(k) ? labels[k] : labels[-1]).ToArray()));
        dispatch.Add(I(OpCodes.Br, labels[-1]));
        foreach (var (key, value) in cases)
        {
            var install = Method(value.Token);
            if (install.Parameters.Count != value.Args || install.ReturnType.FullName != pack.ReturnType.FullName)
                throw new InvalidOperationException("Installer signature mismatch");
            dispatch.Add(labels[key]);
            dispatch.Add(I(OpCodes.Ldstr, "整合包种类：" + value.Name));
            dispatch.Add(I(OpCodes.Ldc_I4_0));
            dispatch.Add(I(OpCodes.Ldstr, "PCL restoration / ModModpack"));
            dispatch.Add(I(OpCodes.Call, Ref("MeloongCore.Logger","Info",3)));
            dispatch.Add(I(OpCodes.Ldarg_0));
            dispatch.Add(I(OpCodes.Ldloc_1));
            if (value.Args >= 3) dispatch.Add(I(OpCodes.Ldloc_2));
            if (value.Args >= 4) dispatch.Add(I(OpCodes.Ldarg_1));
            if (value.Args >= 5) dispatch.Add(I(OpCodes.Ldarg_2));
            dispatch.Add(I(OpCodes.Call, install));
            dispatch.Add(I(OpCodes.Stloc, result));
            dispatch.Add(I(OpCodes.Leave, returnStart));
        }
        InsertAfter(pack, anchor, dispatch);
        Mark(pack, "Restore all seven modpack dispatch paths and a return outside the preserved archive-disposal finally; retain current parsers/installers");

        var modify = Method(0x06000652); var visibility = At(modify,0x12e);
        if (visibility.OpCode != OpCodes.Ldc_I4_2) throw new InvalidOperationException("Unexpected Minecraft card state");
        visibility.OpCode = OpCodes.Ldc_I4_0;
        Mark(modify, "Show the Minecraft version card in the existing-instance modification page");

        var rows = Method(0x0600068b); var rowStart = At(rows,0x21a); var rowEnd = At(rows,0x23b);
        foreach (var x in rows.Body.Instructions.SkipWhile(i => i != rowStart).TakeWhile(i => i != rowEnd).ToArray()) Nop(x);
        var row = new List<Instruction>(); var saveOnly = I(OpCodes.Nop); var common = I(OpCodes.Nop);
        row.Add(I(OpCodes.Ldarg_1)); row.Add(I(OpCodes.Callvirt, Ref("System.Windows.Controls.Panel","get_Children",0)));
        row.Add(I(OpCodes.Ldloc,rows.Body.Variables[7]));
        row.Add(I(OpCodes.Ldarg_0)); row.Add(I(OpCodes.Ldfld,Field("PCL.PageDownloadResourceDetail","TargetResourceType")));
        row.Add(I(OpCodes.Ldc_I4_2)); row.Add(I(OpCodes.Bne_Un,saveOnly));
        // VB generated this adapter for the delegate's object sender. The
        // underlying Install_Click requires MyListItem and cannot bind directly.
        row.Add(I(OpCodes.Ldarg_0)); row.Add(I(OpCodes.Ldftn,Method(0x060006bb)));
        row.Add(I(OpCodes.Newobj,Ref("PCL.MyListItem/ClickEventHandler",".ctor",2)));
        row.Add(I(OpCodes.Ldarg_0)); row.Add(I(OpCodes.Ldftn,Method(0x0600068f)));
        row.Add(I(OpCodes.Newobj,Ref("PCL.MyIconButton/ClickEventHandler",".ctor",2)));
        row.Add(I(OpCodes.Br,common)); row.Add(saveOnly);
        row.Add(I(OpCodes.Ldarg_0)); row.Add(I(OpCodes.Ldftn,Method(0x0600068f)));
        row.Add(I(OpCodes.Newobj,Ref("PCL.MyListItem/ClickEventHandler",".ctor",2)));
        row.Add(I(OpCodes.Ldnull)); row.Add(common); row.Add(I(OpCodes.Ldloc_1));
        row.Add(I(OpCodes.Callvirt,Method(0x060001d7)));
        row.Add(I(OpCodes.Callvirt,Ref("System.Windows.Controls.UIElementCollection","Add",1))); row.Add(I(OpCodes.Pop));
        InsertAfter(rows,rowStart,row);
        Mark(rows,"Restore modpack Install_Click primary action and Save_Click icon action; keep other resource types on Save_Click");

        var drop = Method(0x060017ff);
        foreach (var x in drop.Body.Instructions.Where(i => i.Offset >= 0x28e && i.Offset < 0x2ba)) Nop(x);
        Mark(drop,"Remove the obsolete legal-message special case from dropped-file error handling");
        foreach (var token in new uint[]{0x06001467,0x06001468}) Stub(Method(token),"Remove unused download-denial task");
        var log = Method(0x0600105f);
        foreach (var x in log.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr && (i.Operand as string ?? "").Contains("删除：为遵守相关法律法规")))
            x.Operand = "本地恢复版：已恢复 MC 下载与整合包安装，并关闭 PCL 云端控制及启动器自更新。";
        Mark(log,"Identify the local restoration in the 2.99 update log");
        var flag = target.Types.SelectMany(Types).SelectMany(t => t.Fields).FirstOrDefault(f=>f.Name=="RestrictMcDownload");
        if (flag != null) flag.Constant = false;
    }
    static void Stub(MethodDefinition m, string description)
    {
        if (m.ReturnType.MetadataType != MetadataType.Void) throw new InvalidOperationException("Cannot void-stub " + m.FullName);
        m.Body = new MethodBody(m); m.Body.Instructions.Add(I(OpCodes.Ret));
        Mark(m,description);
    }
    static void DisableCloud()
    {
        foreach (var token in new uint[] {0x0600041f,0x06000420,0x06000421,0x06000423,0x060013de,0x060013e0})
            Stub(Method(token),"Disable launcher update/cloud callback, including remote/cache command replay and updater file replacement");
        var check = Method(0x0600041e);
        check.Body = new MethodBody(check);
        check.Body.Instructions.Add(I(OpCodes.Ldstr,"此本地恢复版已关闭 PCL 云端控制和启动器自更新。游戏下载不受影响。"));
        check.Body.Instructions.Add(I(OpCodes.Ldc_I4_0)); check.Body.Instructions.Add(I(OpCodes.Ldc_I4_1));
        check.Body.Instructions.Add(I(OpCodes.Call, Ref("PCL.ModMain","Hint",3))); check.Body.Instructions.Add(I(OpCodes.Ret));
        Mark(check,"Make the update button explain that launcher updates and cloud control are disabled");

        var export = Method(0x06000422);
        export.Body = new MethodBody(export) { InitLocals = true };
        var dest = new VariableDefinition(target.TypeSystem.String); export.Body.Variables.Add(dest);
        var ret = I(OpCodes.Ret);
        var directory = new TypeReference("System.IO","Directory",target,target.TypeSystem.CoreLibrary);
        var file = new TypeReference("System.IO","File",target,target.TypeSystem.CoreLibrary);
        var directoryInfo = new TypeReference("System.IO","DirectoryInfo",target,target.TypeSystem.CoreLibrary);
        var create = new MethodReference("CreateDirectory",directoryInfo,directory) { HasThis=false };
        create.Parameters.Add(new ParameterDefinition(target.TypeSystem.String));
        var copy = new MethodReference("Copy",target.TypeSystem.Void,file) { HasThis=false };
        copy.Parameters.Add(new ParameterDefinition(target.TypeSystem.String)); copy.Parameters.Add(new ParameterDefinition(target.TypeSystem.String)); copy.Parameters.Add(new ParameterDefinition(target.TypeSystem.Boolean));
        var compareType = new TypeReference("System","StringComparison",target,target.TypeSystem.CoreLibrary,true);
        var equal = new MethodReference("Equals",target.TypeSystem.Boolean,target.TypeSystem.String) { HasThis=false };
        equal.Parameters.Add(new ParameterDefinition(target.TypeSystem.String)); equal.Parameters.Add(new ParameterDefinition(target.TypeSystem.String)); equal.Parameters.Add(new ParameterDefinition(compareType));
        foreach (var x in new[] {
            I(OpCodes.Ldsfld,Field("PCL.ModBase","PathTemp")), I(OpCodes.Call,create), I(OpCodes.Pop),
            I(OpCodes.Ldsfld,Field("PCL.ModBase","PathTemp")), I(OpCodes.Ldstr,"RestoredLauncher.exe"), I(OpCodes.Call,Ref("System.String","Concat",2)), I(OpCodes.Stloc,dest),
            I(OpCodes.Ldsfld,Field("PCL.ModBase","PathExe")), I(OpCodes.Ldloc,dest), I(OpCodes.Ldc_I4_5), I(OpCodes.Call,equal), I(OpCodes.Brtrue,ret),
            I(OpCodes.Ldsfld,Field("PCL.ModBase","PathExe")), I(OpCodes.Ldloc,dest), I(OpCodes.Ldc_I4_1), I(OpCodes.Call,copy), ret }) export.Body.Instructions.Add(x);
        Mark(export,"Export the current restored launcher by local file copy into isolated RestoredLauncher.exe instead of downloading or reusing official Latest.exe");
        foreach (var caller in methods.Where(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.FullName == export.FullName)))
        {
            var paths = caller.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr && i.Operand as string == "Latest.exe").ToArray();
            if (paths.Length == 0) throw new InvalidOperationException("Unexpected launcher export caller: " + caller.FullName);
            foreach (var path in paths) path.Operand = "RestoredLauncher.exe";
            Mark(caller,"Read the current restored launcher from its isolated export cache");
        }
    }
    static void ValidateStructure()
    {
        foreach (var m in methods.Where(m=>m.HasBody))
        {
            var body=m.Body; var set=body.Instructions.ToHashSet();
            foreach(var i in body.Instructions)
            {
                if(i.Operand is Instruction t && !set.Contains(t)) throw new InvalidOperationException("Dangling branch in "+m.FullName);
                if(i.Operand is Instruction[] ts && ts.Any(t=>!set.Contains(t))) throw new InvalidOperationException("Dangling switch in "+m.FullName);
                if(i.Operand is VariableDefinition v && !body.Variables.Contains(v)) throw new InvalidOperationException("Foreign local in "+m.FullName);
                if(i.Operand is MemberReference r && r is not TypeReference && r.DeclaringType.Scope is AssemblyNameReference ar && ar.Name==target.Assembly.Name.Name)
                    throw new InvalidOperationException("Self assembly member reference in "+m.FullName);
            }
            foreach(var eh in body.ExceptionHandlers)
                foreach(var i in new[]{eh.TryStart,eh.TryEnd,eh.HandlerStart,eh.HandlerEnd,eh.FilterStart})
                    if(i!=null && !set.Contains(i)) throw new InvalidOperationException("Dangling exception boundary in "+m.FullName);
        }
    }
    static int Main(string[] args)
    {
        if(args.Length!=3) { Console.Error.WriteLine("Usage: RestorePcl old.exe latest.exe output.exe"); return 2; }
        if(Hash(args[0])!=OldHash || Hash(args[1])!=LatestHash) throw new InvalidOperationException("Input SHA256 does not match audited samples");
        if(string.Equals(Path.GetFullPath(args[2]),Path.GetFullPath(args[0]),StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFullPath(args[2]),Path.GetFullPath(args[1]),StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing to overwrite an original");
        var resolver=new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        if (Environment.GetEnvironmentVariable("PCL_REFERENCE_DIRECTORY") is string referenceDirectory) resolver.AddSearchDirectory(referenceDirectory);
        var framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.8");
        if (Directory.Exists(framework)) { resolver.AddSearchDirectory(framework); resolver.AddSearchDirectory(Path.Combine(framework,"Facades")); }
        using var old=ModuleDefinition.ReadModule(args[0],new ReaderParameters { AssemblyResolver=resolver });
        target=ModuleDefinition.ReadModule(args[1],new ReaderParameters { AssemblyResolver=resolver });
        methods=target.Types.SelectMany(Types).SelectMany(t=>t.Methods).ToList();
        foreach(var t in target.Types.SelectMany(Types))
        {
            members[t.FullName]=t;
            foreach(var f in t.Fields) members[f.FullName]=f;
            foreach(var m in t.Methods) members[m.FullName]=m;
        }
        foreach(var r in target.GetTypeReferences()) members.TryAdd(r.FullName,r);
        foreach(var r in target.GetMemberReferences()) members.TryAdd(r.FullName,r);
        foreach(var m in methods.Where(m=>m.HasBody)) foreach(var i in m.Body.Instructions)
            if(i.Operand is MemberReference r) members.TryAdd(r.FullName,r);
        RestoreDownloads(old); DisableCloud();
        foreach(var m in changed) { m.Body.SimplifyMacros(); m.Body.OptimizeMacros(); }
        ValidateStructure();
        target.Write(args[2],new WriterParameters { WriteSymbols=false });
        using var written = ModuleDefinition.ReadModule(args[2], new ReaderParameters { AssemblyResolver=resolver });
        var writtenMethods = written.Types.SelectMany(Types).SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
        var recordedChanges = changes.Select(c => new {
            token = $"0x{writtenMethods[c.Method.FullName].MetadataToken.ToUInt32():x8}",
            originalToken = c.OriginalToken, method = c.Method.FullName, description = c.Description
        }).ToList();
        var report=new { original=LatestHash, donor=OldHash, output=Hash(args[2]), originalVersion=target.Assembly.Name.Version.ToString(), changedMethods=recordedChanges, validation="Cecil structure validation passed; target program not executed" };
        File.WriteAllText(args[2]+".patch.json",JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true }));
        target.Dispose(); return 0;
    }
}
