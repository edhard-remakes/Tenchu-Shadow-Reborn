using System.Diagnostics;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2) throw new ArgumentException("Usage: RuntimePatch input.dll output.dll");
if (Path.GetFullPath(args[0]) == Path.GetFullPath(args[1]))
    throw new ArgumentException("Input must be preserved; use a separate output path.");
using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true });
var module = assembly.MainModule;
var runtime = module.GetType("RecompOne.Runtime.Runtime");
var method = runtime.Methods.Single(m => m.Name == "PumpBackgroundIfDue");
var instructions = method.Body.Instructions;
var clock = instructions.Single(i => i.OpCode == OpCodes.Call &&
    i.Operand is MethodReference m && m.FullName == "System.Int64 System.Environment::get_TickCount64()");
var gate = instructions.Skip(instructions.IndexOf(clock)).Take(20).Single(i =>
    i.OpCode == OpCodes.Ldc_I4_S && Convert.ToInt32(i.Operand) == 16 && i.Next.OpCode == OpCodes.Conv_I8);

// Preserve the 60 Hz virtual hardware cadence. GetTickCount64 has coarse
// resolution on Windows; checking delta >= 16 can skip whole tick intervals.
// Store Stopwatch timestamps in the existing Int64 field instead, comparing
// against Frequency / 60. No game instructions, frame speed or BIOS are changed.
clock.Operand = module.ImportReference(typeof(Stopwatch).GetMethod(nameof(Stopwatch.GetTimestamp))!);
gate.OpCode = OpCodes.Ldsfld;
gate.Operand = module.ImportReference(typeof(Stopwatch).GetField(nameof(Stopwatch.Frequency))!);
gate.Next.OpCode = OpCodes.Ldc_I8;
gate.Next.Operand = 60L;
method.Body.GetILProcessor().InsertAfter(gate.Next, Instruction.Create(OpCodes.Div));

// Advance the deadline by a fixed interval so small sleep overruns do not
// accumulate into permanent slow motion. Rebase after a whole missed interval
// (including the first frame) instead of running a burst after a pause.
var lastPump = runtime.Fields.Single(f => f.Name == "_lastPumpMs");
var store = instructions.Single(i => i.OpCode == OpCodes.Stsfld &&
    i.Operand is FieldReference f && f.FullName == lastPump.FullName);
var advance = new MethodDefinition("AdvanceHighResolutionPumpClock",
    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
    module.TypeSystem.Int64);
advance.Parameters.Add(new ParameterDefinition("now", ParameterAttributes.None, module.TypeSystem.Int64));
runtime.Methods.Add(advance);
advance.Body.InitLocals = true;
advance.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int64));
var il = advance.Body.GetILProcessor();
var scheduled = Instruction.Create(OpCodes.Ldloc_0);
il.Emit(OpCodes.Ldsfld, lastPump);
il.Emit(OpCodes.Ldsfld, (FieldReference)gate.Operand);
il.Emit(OpCodes.Ldc_I8, 60L);
il.Emit(OpCodes.Div);
il.Emit(OpCodes.Add);
il.Emit(OpCodes.Stloc_0);
il.Emit(OpCodes.Ldarg_0);
il.Emit(OpCodes.Ldloc_0);
il.Emit(OpCodes.Sub);
il.Emit(OpCodes.Ldsfld, (FieldReference)gate.Operand);
il.Emit(OpCodes.Ldc_I8, 60L);
il.Emit(OpCodes.Div);
il.Emit(OpCodes.Blt_S, scheduled);
il.Emit(OpCodes.Ldarg_0);
il.Emit(OpCodes.Ret);
il.Append(scheduled);
il.Emit(OpCodes.Ret);
method.Body.GetILProcessor().InsertBefore(store, Instruction.Create(OpCodes.Call, advance));
assembly.Write(args[1]);

using var check = AssemblyDefinition.ReadAssembly(args[1]);
var fixedMethod = check.MainModule.GetType(runtime.FullName).Methods.Single(m => m.Name == method.Name);
if (fixedMethod.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_TickCount64"))
    throw new InvalidOperationException("Coarse clock reference still present.");
Console.WriteLine("Patched one Windows timing gate: high-resolution fixed deadlines, 60 Hz virtual hardware.");
