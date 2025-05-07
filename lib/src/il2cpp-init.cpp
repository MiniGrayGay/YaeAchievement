#include <Windows.h>
#include <print>
#include <string>
#include <vector>
#include <iterator>
#include <algorithm>
#include <ranges>
#include <unordered_set>
#include <unordered_map>
#include <future>
#include <mutex>
#include <immintrin.h>

#include "globals.h"
#include "Zydis.h"
#include "util.h"

namespace
{
	class DecodedInstruction
	{
	public:
		DecodedInstruction() = default;
		~DecodedInstruction() = default;
		DecodedInstruction(const ZydisDecodedInstruction& instruction) : Instruction(instruction) {}
		DecodedInstruction(const ZydisDecodedInstruction& instruction, ZydisDecodedOperand* operands, uint8_t operandCount) : Instruction(instruction) {
			Operands = { operands, operands + operandCount };
		}
		DecodedInstruction(const uint32_t rva, const ZydisDecodedInstruction& instruction, ZydisDecodedOperand* operands, uint8_t operandCount) : RVA(rva), Instruction(instruction) {
			Operands = { operands, operands + operandCount };
		}

		// copy constructor
		DecodedInstruction(const DecodedInstruction& other) = default;

		// move constructor
		DecodedInstruction(DecodedInstruction&& other) noexcept : RVA(other.RVA), Instruction(other.Instruction), Operands(std::move(other.Operands)) {}

		uint32_t RVA = 0;
		ZydisDecodedInstruction Instruction;
		std::vector<ZydisDecodedOperand> Operands;
	};

	std::span<uint8_t> GetSection(LPCSTR name)
	{
		using namespace Globals;
		if (BaseAddress == 0)
			return {};

		const auto dosHeader = (PIMAGE_DOS_HEADER)BaseAddress;
		const auto ntHeader = (PIMAGE_NT_HEADERS)((uintptr_t)dosHeader + dosHeader->e_lfanew);
		const auto sectionHeader = IMAGE_FIRST_SECTION(ntHeader);

		for (auto i = 0; i < ntHeader->FileHeader.NumberOfSections; i++)
		{
			if (strcmp((char*)sectionHeader[i].Name, name) == 0)
			{
				const auto sectionSize = sectionHeader[i].Misc.VirtualSize;
				const auto virtualAddress = BaseAddress + sectionHeader[i].VirtualAddress;
				return std::span(reinterpret_cast<uint8_t*>(virtualAddress), sectionSize);
			}
		}

		return {};
	}

	/// <summary>
	/// decodes all instruction until next push, ignores branching
	/// </summary>
	/// <param name="address"></param>
	/// <param name="maxInstructions"></param>
	/// <returns>std::vector DecodedInstruction</returns>
	std::vector<DecodedInstruction> DecodeFunction(uintptr_t address, int32_t maxInstructions = -1)
	{
		using namespace Globals;

		std::vector<DecodedInstruction> instructions;

		ZydisDecoder decoder{};
		ZydisDecoderInit(&decoder, ZYDIS_MACHINE_MODE_LONG_64, ZYDIS_STACK_WIDTH_64);

		ZydisDecodedInstruction instruction{};
		ZydisDecoderContext context{};
		ZydisDecodedOperand operands[ZYDIS_MAX_OPERAND_COUNT_VISIBLE]{};

		while (true)
		{
			const auto data = reinterpret_cast<uint8_t*>(address);
			auto status = ZydisDecoderDecodeInstruction(&decoder, &context, data, ZYDIS_MAX_INSTRUCTION_LENGTH, &instruction);
			if (!ZYAN_SUCCESS(status))
			{
				// for skipping jump tables
				address += 1;
				continue;
			}

			status = ZydisDecoderDecodeOperands(&decoder, &context, &instruction, operands, instruction.operand_count_visible);
			if (!ZYAN_SUCCESS(status))
			{
				// for skipping jump tables
				address += 1;
				continue;
			}

			if (instruction.mnemonic == ZYDIS_MNEMONIC_PUSH && !instructions.empty()) {
				break;
			}

			const auto rva = static_cast<uint32_t>(address - BaseAddress);
			instructions.emplace_back(rva, instruction, operands, instruction.operand_count_visible);

			address += instruction.length;

			if (maxInstructions != -1 && instructions.size() >= maxInstructions)
				break;

		}

		return instructions;
	}

	/// <summary>
	/// get the count of data references in the instructions (only second oprand of mov)
	/// </summary>
	/// <param name="instructions"></param>
	/// <returns></returns>
	int32_t GetDataReferenceCount(const std::vector<DecodedInstruction>& instructions)
	{
		return static_cast<int32_t>(std::ranges::count_if(instructions, [](const DecodedInstruction& instr) {
			if (instr.Instruction.mnemonic != ZYDIS_MNEMONIC_MOV)
				return false;

			if (instr.Operands.size() != 2)
				return false;

			const auto& op = instr.Operands[1];

			// access to memory, based off of rip, 32-bit displacement
			return op.type == ZYDIS_OPERAND_TYPE_MEMORY && op.mem.base == ZYDIS_REGISTER_RIP && op.mem.disp.has_displacement;
		}));
	}

	int32_t GetCallCount(const std::vector<DecodedInstruction>& instructions)
	{
		return static_cast<int32_t>(std::ranges::count_if(instructions, [](const DecodedInstruction& instr) {
			return instr.Instruction.mnemonic == ZYDIS_MNEMONIC_CALL;
		}));
	}

	int32_t GetUniqueCallCount(const std::vector<DecodedInstruction>& instructions)
	{
		std::unordered_set<uint32_t> calls;
		for (const auto& instr : instructions)
		{
			if (instr.Instruction.mnemonic == ZYDIS_MNEMONIC_CALL) {
				uint32_t destination = instr.Operands[0].imm.value.s + instr.RVA + instr.Instruction.length;
				calls.insert(destination);
			}
		}

		return static_cast<int32_t>(calls.size());
	}

	int32_t GetCmpImmCount(const std::vector<DecodedInstruction>& instructions)
	{
		return static_cast<int32_t>(std::ranges::count_if(instructions, [](const DecodedInstruction& instr) {
			return instr.Instruction.mnemonic == ZYDIS_MNEMONIC_CMP && instr.Operands[1].type == ZYDIS_OPERAND_TYPE_IMMEDIATE && instr.Operands[1].imm.value.u;
		}));
	}

	void ResolveAchivementCmdId()
	{
		if (Globals::AchievementId != 0)
			return;

		const auto il2cppSection = GetSection("il2cpp");

		std::println("Section Address: 0x{:X}", reinterpret_cast<uintptr_t>(il2cppSection.data()));
		std::println("Section End: 0x{:X}", reinterpret_cast<uintptr_t>(il2cppSection.data() + il2cppSection.size()));

		if (il2cppSection.empty())
			return; // message box?

		const auto candidates = Util::PatternScanAll(il2cppSection, "56 48 83 EC 20 48 89 D0 48 89 CE 80 3D ? ? ? ? 00");
		std::println("Candidates: {}", candidates.size());

		std::vector<std::vector<DecodedInstruction>> filteredInstructions;
		std::ranges::copy_if(
			candidates | std::views::transform([](auto va) { return DecodeFunction(va); }),
			std::back_inserter(filteredInstructions),
			[](const std::vector<DecodedInstruction>& instr) {
			return GetDataReferenceCount(instr) == 5 && GetCallCount(instr) == 10 &&
				GetUniqueCallCount(instr) == 6 && GetCmpImmCount(instr) == 5;
		});

		// should have only one result
		if (filteredInstructions.size() != 1)
		{
			std::println("Filtered Instructions: {}", filteredInstructions.size());
			return;
		}

		const auto& instructions = filteredInstructions[0];
		std::println("RVA: 0x{:08X}", instructions.front().RVA);

		// extract all the non-zero immediate values from the cmp instructions
		std::vector<uint32_t> cmdIds;
		std::ranges::for_each(instructions, [&cmdIds](const DecodedInstruction& instr) {
			if (instr.Instruction.mnemonic == ZYDIS_MNEMONIC_CMP &&
				instr.Operands[1].type == ZYDIS_OPERAND_TYPE_IMMEDIATE &&
				instr.Operands[1].imm.value.u != 0) {
				cmdIds.push_back(static_cast<uint32_t>(instr.Operands[1].imm.value.u));
			}
		});

		for (const auto& cmdId : cmdIds)
		{
			std::println("AchievementId: {}", cmdId);
			Globals::AchievementIdSet.insert(static_cast<uint16_t>(cmdId));
		}


	}

	std::vector<uintptr_t> GetCalls(uint8_t* target)
	{
		const auto il2cppSection = GetSection("il2cpp");
		const auto sectionAddress = reinterpret_cast<uintptr_t>(il2cppSection.data());
		const auto sectionSize = il2cppSection.size();

		std::vector<uintptr_t> callSites;
		const __m128i callOpcode = _mm_set1_epi8(0xE8);
		const size_t simdEnd = sectionSize / 16 * 16;

		for (size_t i = 0; i < simdEnd; i += 16) {
			// load 16 bytes from the current address
			const __m128i chunk = _mm_loadu_si128((__m128i*)(sectionAddress + i));

			// compare the loaded chunk with 0xE8 in all 16 bytes
			const __m128i result = _mm_cmpeq_epi8(chunk, callOpcode);

			// move the comparison results into a mask
			int mask = _mm_movemask_epi8(result);

			while (mask != 0) {
				DWORD first_match_idx = 0;
				_BitScanForward(&first_match_idx, mask); // index of the first set bit (match)

				// index of the instruction
				const size_t instruction_index = i + first_match_idx;

				const int32_t delta = *(int32_t*)(sectionAddress + instruction_index + 1);
				const uintptr_t dest = sectionAddress + instruction_index + 5 + delta;

				if (dest == (uintptr_t)target) {
					callSites.push_back(sectionAddress + instruction_index);
				}

				// clear the bit we just processed and continue with the next match
				mask &= ~(1 << first_match_idx);
			}
		}

		return callSites;
	}

	uintptr_t FindFunctionEntry(uintptr_t address) // not a correct way to find function entry
	{
		__try
		{
			while (true)
			{
				// go back to 'sub rsp' instruction
				uint32_t code = *(uint32_t*)address;
				code &= ~0xFF000000;

				if (_byteswap_ulong(code) == 0x4883EC00) { // sub rsp, ??
					return address;
				}

				address--;
			}

		}
		__except (1) {}

		return address;
	}

	void Resolve_BitConverter_ToUInt16()
	{
		if (Globals::Offset.BitConverter_ToUInt16 != 0) {
			Globals::Offset.BitConverter_ToUInt16 += Globals::BaseAddress;
			return;
		}

		const auto il2cppSection = GetSection("il2cpp");

		std::print("Section Address: 0x{:X}", reinterpret_cast<uintptr_t>(il2cppSection.data()));
		std::println("Section End: 0x{:X}", reinterpret_cast<uintptr_t>(il2cppSection.data() + il2cppSection.size()));

		/*
			mov ecx, 0Fh
			call ThrowHelper.ThrowArgumentNullException
			mov ecx, 0Eh
			mov edx, 16h
			call ThrowHelper.ThrowArgumentOutOfRangeException
			mov ecx, 5
			call ThrowHelper.ThrowArgumentException
		*/
		auto candidates = Util::PatternScanAll(il2cppSection, "B9 0F 00 00 00 E8 ? ? ? ? B9 0E 00 00 00 BA 16 00 00 00 E8 ? ? ? ? B9 05 00 00 00 E8 ? ? ? ?");
		std::println("Candidates: {}", candidates.size());

		std::vector<uintptr_t> filteredEntries;
		std::ranges::copy_if(candidates, std::back_inserter(filteredEntries), [](uintptr_t& entry) {
			entry = FindFunctionEntry(entry);
			return entry % 16 == 0;
		});

		for (const auto& entry : filteredEntries)
		{
			std::println("Entry: 0x{:X}", entry);
		}

		std::println("Looking for call counts...");
		std::mutex mutex;
		std::unordered_map<uintptr_t, int32_t> callCounts;
		// find the call counts to candidate functions
		std::vector<std::future<void>> futures;
		std::ranges::transform(filteredEntries, std::back_inserter(futures), [&](uintptr_t entry) {
			return std::async(std::launch::async, [&](uintptr_t e) {
				const auto callSites = GetCalls((uint8_t*)e);
				std::lock_guard lock(mutex);
				callCounts[e] = callSites.size();
			}, entry);
		});

		for (auto& future : futures) {
			future.get();
		}

		uintptr_t targetEntry = 0;
		for (const auto& [entry, count] : callCounts)
		{
			std::println("Entry: 0x{:X}, RVA: 0x{:08X}, Count: {}", entry, entry - Globals::BaseAddress, count);
			if (count == 3) {
				targetEntry = entry;
			}
		}

		Globals::Offset.BitConverter_ToUInt16 = targetEntry;
	}

	void ResolveInventoryCmdId()
	{
		if (Globals::PlayerStoreId != 0)
			return;

		const auto il2cppSection = GetSection("il2cpp");
		std::println("Section Address: 0x{:X}", reinterpret_cast<uintptr_t>(il2cppSection.data()));
		std::println("Section End: 0x{:X}", reinterpret_cast<uintptr_t>(il2cppSection.data() + il2cppSection.size()));
		
		/*
			cmp r8d, 2
			jz 0x3B
			cmd r8d, 1
			mov rax
		*/

		// look for ItemModule.GetBagManagerByStoreType <- mf got inlined in 5.5
		// we just gon to look for OnPlayerStoreNotify
		const auto candidates = Util::PatternScanAll(il2cppSection, "41 83 F8 02 B8 ? ? ? ? B9 ? ? ? ? 48 0F 45 C1");
		std::println("Candidates: {}", candidates.size());
		if (candidates.empty())
			return;

		uintptr_t pOnPlayerStoreNotify = 0;
		{
			// one of the candidates is OnPlayerStoreNotify
			// search after the pattern to find an arbirary branch
			auto decodedInstructions = candidates | std::views::transform([](auto va) { return DecodeFunction(va, 20); });

			// find the call site with an arbitrary branch (JMP or CALL) after the call
			auto targetInstructions = std::ranges::find_if(decodedInstructions, [](const auto& instr) {
				return std::ranges::any_of(instr, [](const DecodedInstruction& i) {
					return (i.Instruction.mnemonic == ZYDIS_MNEMONIC_JMP || i.Instruction.mnemonic == ZYDIS_MNEMONIC_CALL) &&
						i.Operands.size() == 1 && i.Operands[0].type == ZYDIS_OPERAND_TYPE_REGISTER;
				});
			});

			if (targetInstructions == decodedInstructions.end()) {
				std::println("Failed to find target instruction");
				return;
			}

			// ItemModule.OnPlayerStoreNotify
			const auto& instructions = *targetInstructions;
			pOnPlayerStoreNotify = Globals::BaseAddress + instructions.front().RVA;

			const auto isFunctionEntry = [](uintptr_t va) -> bool {
				auto* code = reinterpret_cast<uint8_t*>(va);
				return (va % 16 == 0 &&
					code[0] == 0x56 && // push rsi
					(*reinterpret_cast<uint32_t*>(&code[1]) & ~0xFF000000) == _byteswap_ulong(0x4883EC00)); // sub rsp, ??
			};

			auto range = std::views::iota(0, 126);
			if (const auto it = std::ranges::find_if(range, [&](int i) { return isFunctionEntry(pOnPlayerStoreNotify - i); });
				it != range.end())
			{
				pOnPlayerStoreNotify -= *it;
			}
			else {
				std::println("Failed to find function entry");
				return;
			}

			std::println("OnPlayerStoreNotify: 0x{:X}", pOnPlayerStoreNotify);
		}

		uintptr_t pOnPacket = 0;
		{
			// get all calls to OnPlayerStoreNotify
			const auto calls = GetCalls(reinterpret_cast<uint8_t*>(pOnPlayerStoreNotify));
			if (calls.size() != 1) {
				std::println("Failed to find call site");
				return;
			}

			// ItemModule.OnPacket - search backwards for function entry
			pOnPacket = calls.front();
			const auto isFunctionEntry = [](uintptr_t va) -> bool {
				auto* code = reinterpret_cast<uint8_t*>(va);
				return (va % 16 == 0 &&
					code[0] == 0x56 && // push rsi
					(*reinterpret_cast<uint32_t*>(&code[1]) & ~0xFF000000) == _byteswap_ulong(0x4883EC00)); // sub rsp, ??
			};

			auto range = std::views::iota(0, 3044);
			if (const auto it = std::ranges::find_if(range, [&](int i) { return isFunctionEntry(pOnPacket - i); });
				it != range.end())
			{
				pOnPacket -= *it;
			}
			else {
				std::println("Failed to find function entry");
				return;
			}

			std::println("OnPacket: 0x{:X}", pOnPacket);
		}

		const auto decodedInstructions = DecodeFunction(pOnPacket);
		uint32_t cmdid = 0;
		std::ranges::for_each(decodedInstructions, [&cmdid, pOnPlayerStoreNotify](const DecodedInstruction& i) {
			static uint32_t immValue = 0; // keep track of the last immediate value

			if (i.Instruction.mnemonic == ZYDIS_MNEMONIC_CMP &&
				i.Operands.size() == 2 &&
				i.Operands[0].type == ZYDIS_OPERAND_TYPE_REGISTER &&
				i.Operands[1].type == ZYDIS_OPERAND_TYPE_IMMEDIATE)
			{
				immValue = static_cast<uint32_t>(i.Operands[1].imm.value.u);
			}

			if (i.Instruction.meta.branch_type == ZYDIS_BRANCH_TYPE_NEAR && i.Operands.size() == 1 &&
				(i.Instruction.mnemonic == ZYDIS_MNEMONIC_JZ || i.Instruction.mnemonic == ZYDIS_MNEMONIC_JNZ)) // jz for true branch, jnz for false branch
			{
				// assume the branching is jz
				uintptr_t branchAddr = Globals::BaseAddress + i.RVA + i.Instruction.length + i.Operands[0].imm.value.s;

				// check if the branch is jnz and adjust the branch address
				if (i.Instruction.mnemonic == ZYDIS_MNEMONIC_JNZ) {
					branchAddr = Globals::BaseAddress + i.RVA + i.Instruction.length;
				}

				// decode the branch address immediately
				const auto instructions = DecodeFunction(branchAddr, 10);
				const auto isMatch = std::ranges::any_of(instructions, [pOnPlayerStoreNotify](const DecodedInstruction& instr) {
					if (instr.Instruction.mnemonic != ZYDIS_MNEMONIC_CALL)
						return false;

					uintptr_t destination = 0;
					ZydisCalcAbsoluteAddress(&instr.Instruction, instr.Operands.data(), Globals::BaseAddress + instr.RVA, &destination);
					return destination == pOnPlayerStoreNotify;
				});

				if (isMatch) {
					cmdid = immValue;
				}

			}
			return cmdid == 0; // stop processing if cmdid is found
		});

		Globals::PlayerStoreId = static_cast<uint16_t>(cmdid);
		std::println("PlayerStoreId: {}", Globals::PlayerStoreId);
	}

	void Resolve_AccountDataItem_UpdateNormalProp()
	{
		if (Globals::Offset.AccountDataItem_UpdateNormalProp != 0) {
			Globals::Offset.AccountDataItem_UpdateNormalProp += Globals::BaseAddress;
			return;
		}

		const auto il2cppSection = GetSection("il2cpp");

		/*
			add   ??, 0FFFFD8EEh
			cmp   ??, 30h
		*/
		auto candidates = Util::PatternScanAll(il2cppSection, "81 ? EE D8 FF FF ? 83 ? 30");
		// should have only one result
		if (candidates.size() != 1)
		{
			std::println("Filtered Instructions: {}", candidates.size());
			return;
		}
		auto fp = candidates[0];

		const auto isFunctionEntry = [](uintptr_t va) -> bool {
			auto* code = reinterpret_cast<uint8_t*>(va);
			/* push rsi */
			/* push rdi */
			return (va % 16 == 0 && code[0] == 0x56 && code[1] == 0x57);
		};

		auto range = std::views::iota(0, 213);
		if (const auto it = std::ranges::find_if(range, [&](int i) { return isFunctionEntry(fp - i); }); it != range.end()) {
			fp -= *it;
		} else {
			std::println("Failed to find function entry");
			return;
		}

		Globals::Offset.AccountDataItem_UpdateNormalProp = fp;
	}

}

bool InitIL2CPP()
{
	std::string buffer;
	buffer.resize(MAX_PATH);
	ZeroMemory(buffer.data(), MAX_PATH);
	const auto pathLength = GetModuleFileNameA(nullptr, buffer.data(), MAX_PATH);
	if (GetLastError() == ERROR_INSUFFICIENT_BUFFER)
	{
		buffer.resize(pathLength);
		ZeroMemory(buffer.data(), pathLength);
		GetModuleFileNameA(nullptr, buffer.data(), pathLength);
	}
	buffer.shrink_to_fit();

	using namespace Globals;
	IsCNREL = buffer.find("YuanShen.exe") != std::string::npos;
	BaseAddress = (uintptr_t)GetModuleHandleA(nullptr);

	std::future<void> resolveFuncFuture = std::async(std::launch::async, Resolve_BitConverter_ToUInt16);
	std::future<void> resolveCmdIdFuture = std::async(std::launch::async, ResolveAchivementCmdId);
	std::future<void> resolveInventoryFuture = std::async(std::launch::async, ResolveInventoryCmdId);
	std::future<void> resolveUpdatePropFuture = std::async(std::launch::async, Resolve_AccountDataItem_UpdateNormalProp);

	resolveFuncFuture.get();
	resolveCmdIdFuture.get();
	resolveInventoryFuture.get();
	resolveUpdatePropFuture.get();

	std::println("BaseAddress: 0x{:X}", BaseAddress);
	std::println("IsCNREL: {:d}", IsCNREL);
	std::println("BitConverter_ToUInt16: 0x{:X}", Offset.BitConverter_ToUInt16);
	std::println("AccountDataItem_UpdateNormalProp: 0x{:X}", Offset.AccountDataItem_UpdateNormalProp);

	if (!AchievementId && AchievementIdSet.empty())
	{
		Util::ErrorDialog("Failed to resolve achievement data");
		return false;
	}

	if (!PlayerStoreId)
	{
		Util::ErrorDialog("Failed to resolve inventory data");
		return false;
	}

	return true;
}
