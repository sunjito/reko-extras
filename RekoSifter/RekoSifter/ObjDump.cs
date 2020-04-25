﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using libopcodes;
using Reko.ImageLoaders.Hunk;

namespace RekoSifter
{

    /// <summary>
    /// This class uses the runtime library used by objdump to disassemble instructions.
    /// </summary>
	public unsafe class ObjDump
    {

        [DllImport("msvcrt.dll", CharSet = CharSet.Ansi, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern int vsprintf(StringBuilder buffer,string format,IntPtr args);

        [DllImport("msvcrt.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        public static extern int _vscprintf(string format,IntPtr ptr);

        private readonly BfdArchInfo arch;
        private StringBuilder buf;

        private readonly string archNameParam;

        private IEnumerable<string> libraries;

        private IntPtr ImportResolver(string libraryName, Assembly asm, DllImportSearchPath? searchPath) {
            switch (libraryName) {
                case "bfd":
                case "opcodes":
                    // find the proper opcodes-* library
                    string libName = libraries.Where(l => l.Contains(archNameParam)).First();
                    return NativeLibrary.Load(libName);
            }
            return IntPtr.Zero;
        }

        private void SetResolver() {
            libraries = Directory.GetFiles(".", "opcodes-*.dll");
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ImportResolver);
        }

        public ObjDump(string arch) {
            this.archNameParam = arch;

            SetResolver();
            BfdArchInfo ai = Bfd.BfdScanArch(arch);
            if(ai == null) {
                throw new NotSupportedException($"This build of binutils doesn't support architecture '{arch}'");
            }

            this.arch = ai;
        }

        public int fprintf(IntPtr h, string fmt, IntPtr args) {
            GCHandle argsH = GCHandle.Alloc(args, GCHandleType.Pinned);
            IntPtr pArgs = argsH.AddrOfPinnedObject();
            
            var sb = new StringBuilder(_vscprintf(fmt, pArgs) + 1);
            vsprintf(sb, fmt, pArgs);

            argsH.Free();

            var formattedMessage = sb.ToString();
            buf.Append(formattedMessage);
            return 0;
        }

        private int BufferReadMemory(ulong memaddr, byte* myaddr, uint length, IntPtr dinfo) {
            DisassembleInfo di = new DisassembleInfo(dinfo.ToPointer());
            return dis_asm.BufferReadMemory(memaddr, myaddr, length, di);
        }

        public string Disassemble(byte[] bytes) {
            buf = new StringBuilder();
            StreamState ss = new StreamState();

            IntPtr ssPtr = Marshal.AllocHGlobal(Marshal.SizeOf<StreamState>());
            Marshal.StructureToPtr(ss, ssPtr, false);

            var disasm_info = new libopcodes.DisassembleInfo();
            dis_asm.InitDisassembleInfo(disasm_info, ssPtr, fprintf);

            fixed(byte* dptr = bytes) {
                disasm_info.Arch = arch.Arch;
                disasm_info.Mach = arch.Mach;
                disasm_info.ReadMemoryFunc = BufferReadMemory;
                disasm_info.Buffer = dptr;
                disasm_info.BufferVma = 0;
                disasm_info.BufferLength = (ulong)bytes.Length;

                dis_asm.DisassembleInitForTarget(disasm_info);

                DisassemblerFtype disasm = dis_asm.Disassembler(arch.Arch, 0, arch.Mach, null);
                if(disasm == null) {
                    string archName = Enum.GetName(typeof(BfdArchitecture), arch);
                    throw new NotSupportedException($"This build of binutils doesn't support architecture '{archName}'");
                }

                ulong pc = 0;
                while(pc < (ulong)bytes.Length) {
                    int insn_size = disasm(pc, disasm_info.__Instance);
                    pc += (ulong)insn_size;
                    
                    buf.AppendLine();

                    break; //only first instruction
                }
            }

            Marshal.FreeHGlobal(ssPtr);

            return buf.ToString();
        }
    }
}