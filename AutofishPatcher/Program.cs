using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;

string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    var hash = SHA256.HashData(stream);
    return Convert.ToHexString(hash);
}

// set TERRARIA_DIR to change the target directory
string terrariaDir = Environment.GetEnvironmentVariable("TERRARIA_DIR")
    ?? (OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library/Application Support/Steam/steamapps/common/Terraria/Terraria.app/Contents/Resources")
        : OperatingSystem.IsWindows()
            ? @"C:\Program Files (x86)\Steam\steamapps\common\Terraria"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local/share/Steam/steamapps/common/Terraria"));

string exeName = "Terraria.exe";
string terrariaExe = Path.Combine(terrariaDir, exeName);
string backupExe = terrariaExe + ".bak";
string hashFile = terrariaExe + ".patched.sha256";

if (File.Exists(backupExe))
{
    if (!File.Exists(hashFile))
    {
        File.Delete(backupExe);
        File.Copy(terrariaExe, backupExe);
        Console.WriteLine("No hash file found, replaced stale backup.");
    }
    else
    {
        var currentHash = HashFile(terrariaExe);
        var patchedHash = File.ReadAllText(hashFile).Trim();

        if (currentHash == patchedHash)
        {
            File.Copy(backupExe, terrariaExe, true);
            Console.WriteLine("Restored original from backup.");
        }
        else
        {
            File.Copy(terrariaExe, backupExe, true);
            Console.WriteLine("Detected game update — backup replaced with new version.");
        }
    }
}
else
{
    File.Copy(terrariaExe, backupExe);
    Console.WriteLine($"Backup created: {backupExe}");
}

Console.WriteLine("Loading Terraria.exe...");
var data = File.ReadAllBytes(terrariaExe);
var mod = ModuleDefMD.Load(data);

var allTypes = mod.Types.ToList();
allTypes.AddRange(allTypes.SelectMany(t => t.NestedTypes).ToList());

T Resolve<T>(T? value, string name) where T : class
{
    if (value == null) { Console.WriteLine($"ERROR: Could not resolve '{name}' — game version may be unsupported"); Environment.Exit(1); }
    return value;
}

var projType = Resolve(allTypes.FirstOrDefault(t => t.Name == "Projectile"), "type Projectile");
var playerType = Resolve(allTypes.FirstOrDefault(t => t.Name == "Player"), "type Player");
var mainType = Resolve(allTypes.FirstOrDefault(t => t.Name == "Main"), "type Main");
var itemType = Resolve(allTypes.FirstOrDefault(t => t.Name == "Item"), "type Item");
var entityType = Resolve(allTypes.FirstOrDefault(t => t.Name == "Entity"), "type Entity");

var aiField = Resolve(projType.Fields.FirstOrDefault(f => f.Name == "ai" && f.FieldType.TypeName == "Single[]"), "Projectile.ai");
var ownerField = Resolve(projType.Fields.FirstOrDefault(f => f.Name == "owner"), "Projectile.owner");
var activeField = Resolve(projType.Fields.FirstOrDefault(f => f.Name == "active"), "Projectile.active");
var aiStyleField = Resolve(projType.Fields.FirstOrDefault(f => f.Name == "aiStyle"), "Projectile.aiStyle");

var mainPlayerField = Resolve(mainType.Fields.FirstOrDefault(f => f.Name == "player" && f.IsStatic), "Main.player");
var myPlayerField = Resolve(mainType.Fields.FirstOrDefault(f => f.Name == "myPlayer" && f.IsStatic), "Main.myPlayer");
var mainProjectileField = Resolve(mainType.Fields.FirstOrDefault(f => f.Name == "projectile" && f.IsStatic), "Main.projectile");

var controlUseItemField = Resolve(playerType.Fields.FirstOrDefault(f => f.Name == "controlUseItem"), "Player.controlUseItem");
var releaseUseItemField = Resolve(playerType.Fields.FirstOrDefault(f => f.Name == "releaseUseItem"), "Player.releaseUseItem");
var itemAnimationField = Resolve(playerType.Fields.FirstOrDefault(f => f.Name == "itemAnimation"), "Player.itemAnimation");

var whoAmIField = Resolve(entityType.Fields.FirstOrDefault(f => f.Name == "whoAmI"), "Entity.whoAmI");
var fishingPoleField = Resolve(itemType.Fields.FirstOrDefault(f => f.Name == "fishingPole"), "Item.fishingPole");

var consumeBait = Resolve(playerType.Methods.FirstOrDefault(m => m.Name == "ItemCheck_CheckFishingBobber_ConsumeBait"), "Player.ConsumeBait");
var pullBobber = Resolve(playerType.Methods.FirstOrDefault(m => m.Name == "ItemCheck_CheckFishingBobber_PullBobber"), "Player.PullBobber");
var killMethod = Resolve(projType.Methods.FirstOrDefault(m => m.Name == "Kill" && m.Parameters.Count == 1), "Projectile.Kill");

Console.WriteLine("\n--- Patch 1: Auto-catch when fish bites (Direct API Call) ---");

var bobberAI = projType.Methods.First(m => m.Name == "AI_061_FishingBobber");
var instrs = bobberAI.Body.Instructions;

int nibbleStart = -1;
for (int i = 0; i < instrs.Count - 6; i++)
{
    if (instrs[i].OpCode == OpCodes.Ldarg_0
        && instrs[i + 1].OpCode == OpCodes.Ldfld && instrs[i + 1].Operand == aiField
        && instrs[i + 2].OpCode == OpCodes.Ldc_I4_1
        && instrs[i + 3].OpCode == OpCodes.Ldelem_R4
        && instrs[i + 4].OpCode == OpCodes.Ldc_R4 && (float)instrs[i + 4].Operand == 0f
        && instrs[i + 5].OpCode == OpCodes.Bge_Un)
    {
        nibbleStart = i;
        break;
    }
}

if (nibbleStart < 0) { Console.WriteLine("ERROR: Could not find nibble handler"); return; }
Console.WriteLine($"  Found nibble handler at instruction {nibbleStart}");

int injectAt = nibbleStart + 6;
var baitLocal = new Local(mod.CorLibTypes.Int32, "autofishBait");
bobberAI.Body.Variables.Add(baitLocal);
var playerLocal = new Local(playerType.ToTypeSig(), "autofishPlayer");
bobberAI.Body.Variables.Add(playerLocal);

var originalCode = instrs[injectAt];
var p1 = new List<Instruction>();

p1.Add(OpCodes.Ldsfld.ToInstruction(mainPlayerField));
p1.Add(OpCodes.Ldarg_0.ToInstruction());
p1.Add(new Instruction(OpCodes.Ldfld, ownerField));
p1.Add(OpCodes.Ldelem_Ref.ToInstruction());
p1.Add(new Instruction(OpCodes.Stloc, playerLocal));

p1.Add(OpCodes.Ldsfld.ToInstruction(myPlayerField));
p1.Add(OpCodes.Ldarg_0.ToInstruction());
p1.Add(new Instruction(OpCodes.Ldfld, ownerField));
p1.Add(new Instruction(OpCodes.Bne_Un, originalCode));

p1.Add(OpCodes.Ldc_I4_0.ToInstruction());
p1.Add(new Instruction(OpCodes.Stloc, baitLocal));

p1.Add(new Instruction(OpCodes.Ldloc, playerLocal));
p1.Add(OpCodes.Ldarg_0.ToInstruction());
p1.Add(new Instruction(OpCodes.Ldloca, baitLocal));
p1.Add(new Instruction(OpCodes.Call, consumeBait));
p1.Add(new Instruction(OpCodes.Brfalse, originalCode));

p1.Add(new Instruction(OpCodes.Ldloc, playerLocal));
p1.Add(OpCodes.Ldarg_0.ToInstruction());
p1.Add(new Instruction(OpCodes.Ldloc, baitLocal));
p1.Add(new Instruction(OpCodes.Call, pullBobber));

p1.Add(OpCodes.Ldarg_0.ToInstruction());
p1.Add(new Instruction(OpCodes.Call, killMethod));

p1.Add(OpCodes.Ret.ToInstruction());

for (int i = 0; i < p1.Count; i++) instrs.Insert(injectAt + i, p1[i]);
Console.WriteLine($"  Injected robust native catch logic");

Console.WriteLine("\n--- Patch 2: Auto-recast when no bobbers ---");

var itemCheck = playerType.Methods.First(m => m.Name == "ItemCheck" && m.Body.Instructions.Count > 2000);
var icInstrs = itemCheck.Body.Instructions;

int gateIdx = -1;
for (int i = 1; i < icInstrs.Count - 2; i++)
{
    if (icInstrs[i].OpCode == OpCodes.Ldarg_0
        && icInstrs[i + 1].OpCode == OpCodes.Ldfld && icInstrs[i + 1].Operand == controlUseItemField
        && icInstrs[i + 2].OpCode == OpCodes.Brfalse
        && icInstrs[i - 1].OpCode == OpCodes.Stfld && icInstrs[i - 1].Operand == releaseUseItemField)
    {
        gateIdx = i;
        break;
    }
}
if (gateIdx < 0)
{
    for (int i = 0; i < icInstrs.Count - 6; i++)
    {
        if (icInstrs[i].OpCode == OpCodes.Ldarg_0
            && icInstrs[i + 1].OpCode == OpCodes.Ldfld && icInstrs[i + 1].Operand == controlUseItemField
            && icInstrs[i + 2].OpCode == OpCodes.Brfalse
            && icInstrs[i + 3].OpCode == OpCodes.Ldarg_0
            && icInstrs[i + 4].OpCode == OpCodes.Ldfld && icInstrs[i + 4].Operand == releaseUseItemField
            && icInstrs[i + 5].OpCode == OpCodes.Brfalse)
        {
            gateIdx = i; break;
        }
    }
}

if (gateIdx < 0) { Console.WriteLine("ERROR: Could not find controlUseItem gate"); return; }
Console.WriteLine($"  Found controlUseItem gate at instruction {gateIdx}");

var loopVar = new Local(mod.CorLibTypes.Int32, "afLoopIdx");
itemCheck.Body.Variables.Add(loopVar);
var projVar = new Local(projType.ToTypeSig(), "afProj");
itemCheck.Body.Variables.Add(projVar);

var gateTarget = icInstrs[gateIdx];
var p2 = new List<Instruction>();

// 动态通过类型匹配获取 Item 本地变量，确保兼容不同的编译器生成
var itemLocal = itemCheck.Body.Variables.First(v => v.Type.TypeName == "Item");

p2.Add(new Instruction(OpCodes.Ldloc, itemLocal));
p2.Add(new Instruction(OpCodes.Ldfld, fishingPoleField));
p2.Add(OpCodes.Ldc_I4_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Ble, gateTarget));

p2.Add(OpCodes.Ldarg_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Ldfld, itemAnimationField));
p2.Add(OpCodes.Ldc_I4_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Bne_Un, gateTarget));

p2.Add(OpCodes.Ldc_I4_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Stloc, loopVar));

var loopStart = new Instruction(OpCodes.Ldsfld, mainProjectileField);
var loopEnd = new Instruction(OpCodes.Ldloc, loopVar);
p2.Add(new Instruction(OpCodes.Br, loopEnd));

p2.Add(loopStart);
p2.Add(new Instruction(OpCodes.Ldloc, loopVar));
p2.Add(OpCodes.Ldelem_Ref.ToInstruction());
p2.Add(new Instruction(OpCodes.Stloc, projVar));

var loopIncrement = new Instruction(OpCodes.Ldloc, loopVar);
p2.Add(new Instruction(OpCodes.Ldloc, projVar));
p2.Add(new Instruction(OpCodes.Ldfld, activeField));
p2.Add(new Instruction(OpCodes.Brfalse, loopIncrement));

p2.Add(new Instruction(OpCodes.Ldloc, projVar));
p2.Add(new Instruction(OpCodes.Ldfld, ownerField));
p2.Add(OpCodes.Ldarg_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Ldfld, whoAmIField));
p2.Add(new Instruction(OpCodes.Bne_Un, loopIncrement));

p2.Add(new Instruction(OpCodes.Ldloc, projVar));
p2.Add(new Instruction(OpCodes.Ldfld, aiStyleField));
p2.Add(new Instruction(OpCodes.Ldc_I4, 61));
p2.Add(new Instruction(OpCodes.Bne_Un, loopIncrement));

p2.Add(new Instruction(OpCodes.Br, gateTarget));

p2.Add(loopIncrement);
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(OpCodes.Add.ToInstruction());
p2.Add(new Instruction(OpCodes.Stloc, loopVar));

p2.Add(loopEnd);
p2.Add(new Instruction(OpCodes.Ldc_I4, 1000));
p2.Add(new Instruction(OpCodes.Blt, loopStart));

p2.Add(OpCodes.Ldarg_0.ToInstruction());
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(new Instruction(OpCodes.Stfld, controlUseItemField));

p2.Add(OpCodes.Ldarg_0.ToInstruction());
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(new Instruction(OpCodes.Stfld, releaseUseItemField));

var firstNewInstr = p2[0];
for (int i = 0; i < gateIdx; i++)
{
    if (icInstrs[i].Operand == gateTarget)
        icInstrs[i].Operand = firstNewInstr;
}

for (int i = 0; i < p2.Count; i++) icInstrs.Insert(gateIdx + i, p2[i]);
Console.WriteLine($"  Injected {p2.Count} instructions for robust auto-recast");

Console.WriteLine("\nSaving patched Terraria.exe...");
var writerOptions = new dnlib.DotNet.Writer.ModuleWriterOptions(mod) { MetadataOptions = { Flags = dnlib.DotNet.Writer.MetadataFlags.PreserveAll } };
mod.Write(terrariaExe, writerOptions);
File.WriteAllText(hashFile, HashFile(terrariaExe));

Console.WriteLine("\nDone! Perfected Native Autofish patch applied.");
