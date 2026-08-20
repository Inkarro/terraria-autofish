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

var mainProjectileField = Resolve(mainType.Fields.FirstOrDefault(f => f.Name == "projectile" && f.IsStatic), "Main.projectile");
var controlUseItemField = Resolve(playerType.Fields.FirstOrDefault(f => f.Name == "controlUseItem"), "Player.controlUseItem");
var releaseUseItemField = Resolve(playerType.Fields.FirstOrDefault(f => f.Name == "releaseUseItem"), "Player.releaseUseItem");
var itemAnimationField = Resolve(playerType.Fields.FirstOrDefault(f => f.Name == "itemAnimation"), "Player.itemAnimation");
var whoAmIField = Resolve(entityType.Fields.FirstOrDefault(f => f.Name == "whoAmI"), "Entity.whoAmI");
var fishingPoleField = Resolve(itemType.Fields.FirstOrDefault(f => f.Name == "fishingPole"), "Item.fishingPole");

Console.WriteLine("\n--- Unified Patch: 100% Native Mouse Simulation ---");

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

var hasBobberLocal = new Local(mod.CorLibTypes.Boolean, "hasBobber");
itemCheck.Body.Variables.Add(hasBobberLocal);
var shouldReelInLocal = new Local(mod.CorLibTypes.Boolean, "shouldReelIn");
itemCheck.Body.Variables.Add(shouldReelInLocal);
var loopVar = new Local(mod.CorLibTypes.Int32, "afLoopIdx");
itemCheck.Body.Variables.Add(loopVar);
var projVar = new Local(projType.ToTypeSig(), "afProj");
itemCheck.Body.Variables.Add(projVar);

var gateTarget = icInstrs[gateIdx];
var p2 = new List<Instruction>();

var itemLocal = itemCheck.Body.Variables.First(v => v.Type.TypeName == "Item");

p2.Add(new Instruction(OpCodes.Ldloc, itemLocal));
p2.Add(new Instruction(OpCodes.Ldfld, fishingPoleField));
p2.Add(OpCodes.Ldc_I4_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Ble, gateTarget));

p2.Add(OpCodes.Ldarg_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Ldfld, itemAnimationField));
p2.Add(OpCodes.Ldc_I4_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Bne_Un, gateTarget));

// hasBobber = false, shouldReelIn = false
p2.Add(OpCodes.Ldc_I4_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Stloc, hasBobberLocal));
p2.Add(OpCodes.Ldc_I4_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Stloc, shouldReelInLocal));

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

// 发现自己的浮标，hasBobber = true
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(new Instruction(OpCodes.Stloc, hasBobberLocal));

// 检查 ai[1] < 0f (有鱼咬钩)
p2.Add(new Instruction(OpCodes.Ldloc, projVar));
p2.Add(new Instruction(OpCodes.Ldfld, aiField));
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(OpCodes.Ldelem_R4.ToInstruction());
p2.Add(new Instruction(OpCodes.Ldc_R4, 0f));
p2.Add(new Instruction(OpCodes.Bge_Un, loopIncrement));

// 检查 ai[1] >= -45f (等待假鱼弹幕动画游到附近)
p2.Add(new Instruction(OpCodes.Ldloc, projVar));
p2.Add(new Instruction(OpCodes.Ldfld, aiField));
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(OpCodes.Ldelem_R4.ToInstruction());
p2.Add(new Instruction(OpCodes.Ldc_R4, -45f));
p2.Add(new Instruction(OpCodes.Blt_Un, loopIncrement));

// 需要收杆，shouldReelIn = true
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(new Instruction(OpCodes.Stloc, shouldReelInLocal));

p2.Add(loopIncrement);
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(OpCodes.Add.ToInstruction());
p2.Add(new Instruction(OpCodes.Stloc, loopVar));

p2.Add(loopEnd);
p2.Add(new Instruction(OpCodes.Ldc_I4, 1000));
p2.Add(new Instruction(OpCodes.Blt, loopStart));

// 循环结束判断
var triggerClick = OpCodes.Ldarg_0.ToInstruction();

p2.Add(new Instruction(OpCodes.Ldloc, shouldReelInLocal));
p2.Add(new Instruction(OpCodes.Brtrue, triggerClick)); // 有鱼上钩，触发鼠标点击收杆

p2.Add(new Instruction(OpCodes.Ldloc, hasBobberLocal));
p2.Add(new Instruction(OpCodes.Brtrue, gateTarget)); // 有浮标但在等待中，什么也不做

// 如果以上都不满足（水里没浮标），同样触发下面的鼠标点击用来抛竿
p2.Add(triggerClick);
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(new Instruction(OpCodes.Stfld, controlUseItemField)); // 模拟鼠标按住

p2.Add(OpCodes.Ldarg_0.ToInstruction());
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(new Instruction(OpCodes.Stfld, releaseUseItemField)); // 模拟鼠标松开

var firstNewInstr = p2[0];
for (int i = 0; i < gateIdx; i++)
{
    if (icInstrs[i].Operand == gateTarget)
        icInstrs[i].Operand = firstNewInstr;
}

for (int i = 0; i < p2.Count; i++) icInstrs.Insert(gateIdx + i, p2[i]);
Console.WriteLine($"  Injected {p2.Count} instructions for complete human emulation");

Console.WriteLine("\nSaving patched Terraria.exe...");
var writerOptions = new dnlib.DotNet.Writer.ModuleWriterOptions(mod) { MetadataOptions = { Flags = dnlib.DotNet.Writer.MetadataFlags.PreserveAll } };
mod.Write(terrariaExe, writerOptions);
File.WriteAllText(hashFile, HashFile(terrariaExe));

Console.WriteLine("\nDone! Perfect Unified Autofish patch applied.");
