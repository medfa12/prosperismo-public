cmake_minimum_required(VERSION 3.12...4.3)

if(CMAKE_VERSION VERSION_GREATER_EQUAL 4.0)
	set(CMAKE_POLICY_VERSION_MINIMUM 3.5 CACHE STRING "Minimum policy version for bundled dependencies")
endif()

project(Prosperismo)

if(CMAKE_SYSTEM_NAME MATCHES ".*Linux")
	set(LINUX TRUE)
endif()

if (NOT (WIN32 OR LINUX OR APPLE))
    message(FATAL_ERROR "only Windows, Linux, and macOS builds are supported")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_CXX_SCAN_FOR_MODULES OFF)

include(utils.cmake)
include(CTest)

option(KYTY_ENABLE_CLANG_TIDY "Run clang-tidy checks during builds" OFF)

set(KYTY_THIRD_PARTY_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../3rdparty")

set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS ON)

include(TestBigEndian)

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
	message(FATAL_ERROR "only 64-bit builds are supported")
endif()

test_big_endian(BIG_ENDIAN)
if(BIG_ENDIAN)
	set(KYTY_ENDIAN KYTY_ENDIAN_BIG)
else()
	set(KYTY_ENDIAN KYTY_ENDIAN_LITTLE)
endif()

if(CMAKE_BUILD_TYPE MATCHES Debug)
	set(KYTY_BUILD KYTY_BUILD_DEBUG)
else()
	set(KYTY_BUILD KYTY_BUILD_RELEASE)
endif()

if(LINUX OR APPLE)
	# macOS rides the POSIX/Linux code paths until it gets a dedicated platform
	set(KYTY_PLATFORM KYTY_PLATFORM_LINUX)
else()
	set(KYTY_PLATFORM KYTY_PLATFORM_WINDOWS)
endif()

set(CLANG FALSE)
set(GCC FALSE)
set(KYTY_CLANG_CL FALSE)
get_filename_component(KYTY_CXX_COMPILER_NAME "${CMAKE_CXX_COMPILER}" NAME_WE)
string(TOLOWER "${KYTY_CXX_COMPILER_NAME}" KYTY_CXX_COMPILER_NAME)

if(CMAKE_CXX_COMPILER_ID MATCHES "^(Apple)?Clang$")
	set(CLANG TRUE)
	set(KYTY_COMPILER CLANG)
	if(WIN32 AND KYTY_CXX_COMPILER_NAME STREQUAL "clang-cl")
		set(KYTY_CLANG_CL TRUE)
		set(KYTY_LINKER LLD_LINK)
	elseif(APPLE)
		set(KYTY_LINKER LD64) # Apple's default linker; lld flags don't apply
	else()
		set(KYTY_LINKER LLD)
	endif()
	if ((CMAKE_AR MATCHES "llvm") AND (CMAKE_CXX_COMPILER_VERSION VERSION_LESS 12.0.0) AND (CMAKE_CXX_COMPILER MATCHES "winlibs"))
		string(REPLACE "llvm-ar.exe" "ar.exe" CMAKE_AR ${CMAKE_AR}) # llvm-ar.exe 11.0.0 from winlibs is broken
	endif()
elseif(CMAKE_CXX_COMPILER_ID STREQUAL "GNU")
	set(GCC TRUE)
	set(KYTY_COMPILER GCC)
	set(KYTY_LINKER LD)
else()
	message(FATAL_ERROR "only Clang and GCC are supported; found ${CMAKE_CXX_COMPILER_ID}")
endif()

set(KYTY_PROJECT_NAME "Emulator" CACHE STRING "Project name suffix")

string(TOLOWER ${KYTY_COMPILER} KYTY_COMPILER_ID)
string(TOLOWER ${KYTY_LINKER} KYTY_LINKER_ID)

if (CLANG AND (KYTY_LINKER STREQUAL LLD))
	set(KYTY_LD_OPTIONS "-fuse-ld=lld")
endif()

if (KYTY_LINKER STREQUAL LD AND NOT LINUX)
	set(KYTY_LD_OPTIONS "-Wl,--image-base=0x100000000000")
endif()

project(Prosperismo${KYTY_PROJECT_NAME}${CMAKE_BUILD_TYPE}${KYTY_COMPILER} VERSION 0.2.2)

include_directories(	
	${KYTY_THIRD_PARTY_DIR}/gtest/include
	${KYTY_THIRD_PARTY_DIR}/gtest
	${KYTY_THIRD_PARTY_DIR}/fmt/include
	${KYTY_THIRD_PARTY_DIR}/magic_enum/include/magic_enum
	${PROJECT_BINARY_DIR}
	${CMAKE_CURRENT_SOURCE_DIR}
)

set(KYTY_VERSION "${PROJECT_VERSION}")

configure_file(
  ${PROJECT_SOURCE_DIR}/cmake_config.h.in
  ${PROJECT_BINARY_DIR}/cmake_config.h
  )

find_package(Git)
  
add_custom_target( KytyGitVersion
    COMMAND ${CMAKE_COMMAND} 
	-D INPUT_FILE=${CMAKE_CURRENT_SOURCE_DIR}/kytyGitVersion.h.in
    -D OUTPUT_FILE=${CMAKE_CURRENT_BINARY_DIR}/kytyGitVersion.h
	-D GIT_EXECUTABLE=${GIT_EXECUTABLE}
	-D GIT_WORKING_DIRECTORY=${CMAKE_CURRENT_SOURCE_DIR}
	-P ${CMAKE_CURRENT_SOURCE_DIR}/generate_version.cmake
	COMMENT "Generate kytyGitVersion.h"
)

if (CMAKE_CXX_COMPILER_VERSION VERSION_GREATER_EQUAL 12.0.0)
	list(APPEND KYTY_IWYU 
		prosperismo_emulator
		common
		#launcher
	)
	list(APPEND KYTY_CLANG_TIDY
		prosperismo_emulator
		#common
		launcher
	)
endif()

option(KYTY_BUILD_LAUNCHER "Build the legacy Qt launcher" OFF)
  
config_compiler_and_linker()  
  
add_subdirectory("${KYTY_THIRD_PARTY_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/3rdparty")
add_subdirectory(common)

file(GLOB kyty_emulator_src CONFIGURE_DEPENDS
	libs/*.cpp
	libs/*.h
	graphics/*.cpp
	graphics/*.h
	graphics/guest_gpu/*.cpp
	graphics/guest_gpu/*.h
	graphics/guest_gpu/command_processor/*.cpp
	graphics/guest_gpu/command_processor/*.h
	graphics/host_gpu/*.cpp
	graphics/host_gpu/*.h
	graphics/host_gpu/renderer/*.cpp
	graphics/host_gpu/renderer/*.h
	graphics/host_gpu/renderer/cache/*.cpp
	graphics/host_gpu/renderer/cache/*.h
	graphics/host_gpu/renderer/image/*.cpp
	graphics/host_gpu/renderer/image/*.h
	graphics/host_gpu/renderer/pipeline/*.cpp
	graphics/host_gpu/renderer/pipeline/*.h
	graphics/shader/*.cpp
	graphics/shader/*.h
	graphics/shader/recompiler/*.cpp
	graphics/shader/recompiler/*.h
	graphics/shader/recompiler/cfg/*.cpp
	graphics/shader/recompiler/cfg/*.h
	graphics/shader/recompiler/decompiler/*.cpp
	graphics/shader/recompiler/decompiler/*.h
	graphics/shader/recompiler/emitter/*.cpp
	graphics/shader/recompiler/emitter/*.h
	graphics/shader/recompiler/ir/*.cpp
	graphics/shader/recompiler/ir/*.h
	graphics/presentation/*.cpp
	graphics/presentation/*.h
	graphics/presentation/window/*.cpp
	graphics/presentation/window/*.h
	kernel/*.cpp
	kernel/*.h
	loader/*.cpp
	loader/*.h
)

find_program(KYTY_GLSLANG_VALIDATOR glslangValidator REQUIRED)
set(gpu_tiler_shader_dir "${CMAKE_CURRENT_SOURCE_DIR}/graphics/host_gpu/shaders")
set(gpu_tiler_generated_dir "${PROJECT_BINARY_DIR}/gpu_tiler_shaders")
set(gpu_tiler_shader_names
	standard256
	standard4
	standard4_3d
	standard64
	standard64_3d
	prt
	prt_3d
	render_target
	depth
	promote_d16
	demote_d16
	swap_bgra16
)
file(GLOB gpu_tiler_shader_includes CONFIGURE_DEPENDS "${gpu_tiler_shader_dir}/gpu_tiler_*.inc")
foreach(shader_name IN LISTS gpu_tiler_shader_names)
	set(shader_source "${gpu_tiler_shader_dir}/gpu_tiler_${shader_name}.comp")
	set(shader_spv "${gpu_tiler_generated_dir}/gpu_tiler_${shader_name}.spv")
	set(shader_header "${gpu_tiler_generated_dir}/gpu_tiler_${shader_name}_spv.h")
	string(TOUPPER "GPU_TILER_${shader_name}_SPV" shader_symbol)
	add_custom_command(
		OUTPUT "${shader_header}"
		COMMAND ${CMAKE_COMMAND} -E make_directory "${gpu_tiler_generated_dir}"
		COMMAND "${KYTY_GLSLANG_VALIDATOR}" -V --target-env vulkan1.0 -Os
		        "-I${gpu_tiler_shader_dir}" -o "${shader_spv}" "${shader_source}"
		COMMAND ${CMAKE_COMMAND} -DINPUT=${shader_spv} -DOUTPUT=${shader_header}
		        -DSYMBOL=${shader_symbol} -P "${CMAKE_CURRENT_SOURCE_DIR}/embed_spirv.cmake"
		DEPENDS "${shader_source}" ${gpu_tiler_shader_includes}
		VERBATIM
	)
	list(APPEND gpu_tiler_shader_headers "${shader_header}")
endforeach()
list(APPEND kyty_emulator_src ${gpu_tiler_shader_headers})

set(gpu_blit_generated_dir "${PROJECT_BINARY_DIR}/gpu_blit_shaders")
set(gpu_blit_shader_sources
	"${gpu_tiler_shader_dir}/gpu_blit_fs_triangle.vert"
	"${gpu_tiler_shader_dir}/gpu_blit_color_to_ms_depth.frag"
)
foreach(shader_source IN LISTS gpu_blit_shader_sources)
	get_filename_component(shader_name "${shader_source}" NAME_WE)
	set(shader_spv "${gpu_blit_generated_dir}/${shader_name}.spv")
	set(shader_header "${gpu_blit_generated_dir}/${shader_name}_spv.h")
	string(TOUPPER "${shader_name}_SPV" shader_symbol)
	add_custom_command(
		OUTPUT "${shader_header}"
		COMMAND ${CMAKE_COMMAND} -E make_directory "${gpu_blit_generated_dir}"
		COMMAND "${KYTY_GLSLANG_VALIDATOR}" -V --target-env vulkan1.0 -Os
		        "-I${gpu_tiler_shader_dir}" -o "${shader_spv}" "${shader_source}"
		COMMAND ${CMAKE_COMMAND} -DINPUT=${shader_spv} -DOUTPUT=${shader_header}
		        -DSYMBOL=${shader_symbol} -P "${CMAKE_CURRENT_SOURCE_DIR}/embed_spirv.cmake"
		DEPENDS "${shader_source}"
		VERBATIM
	)
	list(APPEND gpu_blit_shader_headers "${shader_header}")
endforeach()
list(APPEND kyty_emulator_src ${gpu_blit_shader_headers})

list(APPEND kyty_emulator_src
	cliOptions.h
	cliOptions.cpp
	emulator.h
	emulator.cpp
)
list(REMOVE_DUPLICATES kyty_emulator_src)

if (KYTY_CLANG_CL)
	set_source_files_properties(${kyty_emulator_src} PROPERTIES COMPILE_FLAGS "-Wno-pragma-pack -Wno-deprecated-declarations -D_TIMESPEC_DEFINED")
endif()

set(kyty_emulator_link_libraries common Vulkan::Headers spirv-tools-opt spirv-tools SDL2-static xxhash FFmpeg::ffmpeg fmt::fmt nlohmann_json::nlohmann_json LibAtrac9)

# Linux system libraries required by the static FFmpeg archive.
if(LINUX)
	find_package(Threads REQUIRED)
	list(APPEND kyty_emulator_link_libraries m ${CMAKE_DL_LIBS} Threads::Threads)

	# Optional FFmpeg dependencies.
	find_package(ZLIB)
	if(ZLIB_FOUND)
		list(APPEND kyty_emulator_link_libraries ZLIB::ZLIB)
	endif()
	find_library(KYTY_BZ2_LIBRARY bz2)
	if(KYTY_BZ2_LIBRARY)
		list(APPEND kyty_emulator_link_libraries ${KYTY_BZ2_LIBRARY})
	endif()
	find_library(KYTY_LZMA_LIBRARY lzma)
	if(KYTY_LZMA_LIBRARY)
		list(APPEND kyty_emulator_link_libraries ${KYTY_LZMA_LIBRARY})
	endif()
endif()

set(inc_headers
	${CMAKE_CURRENT_SOURCE_DIR}
	${KYTY_THIRD_PARTY_DIR}/SDL2/include
	${KYTY_THIRD_PARTY_DIR}/VulkanMemoryAllocator/include
	${KYTY_THIRD_PARTY_DIR}/SPIRV-Tools/include
	${KYTY_THIRD_PARTY_DIR}/fmt/include
	${KYTY_THIRD_PARTY_DIR}/xxhash
	${KYTY_THIRD_PARTY_DIR}/cpuinfo/include
	${KYTY_THIRD_PARTY_DIR}/stb
)

if (KYTY_CLANG_CL)
	list(APPEND kyty_emulator_link_libraries winpthread)
	list(APPEND inc_headers 
		${KYTY_THIRD_PARTY_DIR}/winpthread/include
	)
endif()

list(APPEND check_headers
	${CMAKE_CURRENT_SOURCE_DIR}
)

function(add_kyty_full_emulator_test target source)
	add_executable(${target} EXCLUDE_FROM_ALL ${source} ${kyty_emulator_src})
	target_link_libraries(${target} ${kyty_emulator_link_libraries})
	target_include_directories(${target} PRIVATE ${inc_headers})
	if (WIN32)
		target_link_libraries(${target} iphlpapi)
	endif()
	if (KYTY_CLANG_CL)
		target_link_libraries(${target} onecore)
		add_custom_command(TARGET ${target} POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different "${KYTY_THIRD_PARTY_DIR}/winpthread/bin/libwinpthread-1.dll" $<TARGET_FILE_DIR:${target}>/libwinpthread-1.dll)
	endif()
	# The macOS x86_64 guest address space needs its .zerofill segments anchored
	# by linker flags, or the kernel kills the binary on load (posix_spawn EIO).
	configure_macos_guest_address_space(${target})
endfunction()

function(configure_macos_guest_address_space target)
	if(APPLE AND (CMAKE_OSX_ARCHITECTURES STREQUAL "x86_64" OR
			(NOT CMAKE_OSX_ARCHITECTURES AND CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|AMD64)$")))
		target_sources(${target} PRIVATE kernel/macosGuestAddressSpace.cpp)
		target_compile_definitions(${target} PRIVATE KYTY_LINKED_GUEST_ADDRESS_SPACE=1)
		target_link_options(${target} PRIVATE
			-Wl,-ld_classic,-no_pie,-no_fixup_chains,-no_huge,-pagezero_size,0x40000,-segaddr,SYSTEM_MANAGED,0x40000,-segaddr,SYSTEM_RESERVED,0x7ffffc000,-segaddr,USER_AREA,0x7000000000,-image_base,0x700000000000)
	endif()
endfunction()

add_kyty_full_emulator_test(shader_cfg_tests ../tests/shaderCfgTests.cpp)

add_executable(cli_options_tests EXCLUDE_FROM_ALL
	../tests/CliOptionsTests.cpp
	cliOptions.cpp
)
target_link_libraries(cli_options_tests common)
target_include_directories(cli_options_tests PRIVATE ${inc_headers})

add_executable(scalar_provenance_tests EXCLUDE_FROM_ALL
	../tests/ScalarProvenanceTests.cpp
	graphics/host_gpu/hostMemory.cpp
	graphics/shader/recompiler/ir/ReadLaneElimination.cpp
	graphics/shader/recompiler/ir/ScalarProvenance.cpp
	graphics/shader/recompiler/ir/SrtWalker.cpp
)
target_link_libraries(scalar_provenance_tests fmt::fmt)
target_include_directories(scalar_provenance_tests PRIVATE ${inc_headers})

add_executable(page_manager_tests EXCLUDE_FROM_ALL
	../tests/PageManagerTests.cpp
	graphics/host_gpu/pageManager.cpp
)
target_include_directories(page_manager_tests PRIVATE ${inc_headers})

add_executable(bit_array_tests EXCLUDE_FROM_ALL
	../tests/BitArrayTests.cpp
)
target_include_directories(bit_array_tests PRIVATE ${inc_headers})

add_executable(color_metadata_state_tests EXCLUDE_FROM_ALL
	../tests/ColorMetadataStateTests.cpp
)
target_include_directories(color_metadata_state_tests PRIVATE ${inc_headers})

add_executable(memory_tracker_tests EXCLUDE_FROM_ALL
	../tests/MemoryTrackerTests.cpp
	graphics/host_gpu/pageManager.cpp
	graphics/host_gpu/memoryTracker.cpp
)
target_link_libraries(memory_tracker_tests fmt::fmt common)
target_include_directories(memory_tracker_tests PRIVATE ${inc_headers})

add_executable(shader_vertex_metadata_tests EXCLUDE_FROM_ALL
	../tests/ShaderVertexMetadataTests.cpp
	graphics/host_gpu/hostMemory.cpp
	graphics/shader/shaderVertexMetadata.cpp
)
target_include_directories(shader_vertex_metadata_tests PRIVATE ${inc_headers})

add_executable(shader_stage_runtime_tests EXCLUDE_FROM_ALL
	../tests/ShaderStageRuntimeTests.cpp
	graphics/guest_gpu/gpu_format.cpp
	graphics/host_gpu/hostMemory.cpp
	graphics/shader/shaderStageRuntime.cpp
	graphics/shader/recompiler/ir/ResourceMaterialization.cpp
	graphics/shader/recompiler/ir/ScalarProvenance.cpp
	graphics/shader/recompiler/ir/SrtWalker.cpp
)
target_link_libraries(shader_stage_runtime_tests fmt::fmt)
target_include_directories(shader_stage_runtime_tests PRIVATE ${inc_headers})

add_executable(resource_tracking_tests EXCLUDE_FROM_ALL
	../tests/ResourceTrackingTests.cpp
	graphics/guest_gpu/gpu_format.cpp
	graphics/host_gpu/hostMemory.cpp
	graphics/shader/recompiler/ir/ScalarProvenance.cpp
	graphics/shader/recompiler/ir/SrtWalker.cpp
	graphics/shader/recompiler/ir/SrtPatcher.cpp
	graphics/shader/recompiler/ir/ResourceTracking.cpp
	graphics/shader/recompiler/ir/ResourceMaterialization.cpp
	graphics/shader/recompiler/ir/ShaderInfoCollection.cpp
	graphics/shader/recompiler/ir/BindingLayout.cpp
)
target_link_libraries(resource_tracking_tests fmt::fmt)
target_include_directories(resource_tracking_tests PRIVATE ${inc_headers})

add_executable(resource_mutex_tests EXCLUDE_FROM_ALL
	../tests/ResourceMutexTests.cpp
	graphics/host_gpu/renderer/cache/resourceMutex.cpp
)
target_link_libraries(resource_mutex_tests common)
target_include_directories(resource_mutex_tests PRIVATE ${inc_headers})

add_executable(audio_out2_port_tests EXCLUDE_FROM_ALL
	../tests/AudioOut2PortTests.cpp
	libs/libAudio2.cpp
	loader/timer.cpp
)
target_link_libraries(audio_out2_port_tests common fmt::fmt)
target_include_directories(audio_out2_port_tests PRIVATE ${inc_headers})

add_executable(audio_out_primary_contract_tests EXCLUDE_FROM_ALL
	../tests/AudioOutPrimaryContractTests.cpp
	libs/audio.cpp
)
target_link_libraries(audio_out_primary_contract_tests common fmt::fmt SDL2-static)
target_include_directories(audio_out_primary_contract_tests PRIVATE ${inc_headers})

add_executable(av_player_init_contract_tests EXCLUDE_FROM_ALL
	../tests/AvPlayerInitContractTests.cpp
	libs/avPlayer.cpp
)
target_link_libraries(av_player_init_contract_tests common fmt::fmt FFmpeg::ffmpeg)
target_include_directories(av_player_init_contract_tests PRIVATE ${inc_headers})

add_executable(ajm_init_contract_tests EXCLUDE_FROM_ALL
	../tests/AjmInitContractTests.cpp
	libs/ajm.cpp
)
target_link_libraries(ajm_init_contract_tests common fmt::fmt FFmpeg::ffmpeg LibAtrac9)
target_include_directories(ajm_init_contract_tests PRIVATE ${inc_headers})

add_executable(event_queue_lifetime_tests EXCLUDE_FROM_ALL
	../tests/EventQueueLifetimeTests.cpp
	kernel/eventQueue.cpp
	loader/timer.cpp
)

add_executable(event_queue_trigger_contract_tests EXCLUDE_FROM_ALL
	../tests/EventQueueTriggerContractTests.cpp
	kernel/eventQueue.cpp
	loader/timer.cpp
)
target_link_libraries(event_queue_trigger_contract_tests common fmt::fmt)
target_include_directories(event_queue_trigger_contract_tests PRIVATE ${inc_headers})

add_executable(native_primitive_replay_tests EXCLUDE_FROM_ALL
	../tests/NativePrimitiveReplayTests.cpp
)
target_include_directories(native_primitive_replay_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(event_queue_lifetime_tests common fmt::fmt)
target_include_directories(event_queue_lifetime_tests PRIVATE ${inc_headers})

add_executable(hle_contract_tests EXCLUDE_FROM_ALL
	../tests/HleContractTests.cpp
	kernel/eventFlag.cpp
	kernel/uuid.cpp
)
target_link_libraries(hle_contract_tests common fmt::fmt)
target_include_directories(hle_contract_tests PRIVATE ${inc_headers})

add_executable(guest_printf_contract_tests EXCLUDE_FROM_ALL
	../tests/GuestPrintfContractTests.cpp
	libs/guestPrintf.cpp
)
target_link_libraries(guest_printf_contract_tests common fmt::fmt)
target_include_directories(guest_printf_contract_tests PRIVATE ${inc_headers})

add_executable(libc_fflush_contract_tests EXCLUDE_FROM_ALL
	../tests/LibcFflushContractTests.cpp
)
target_link_libraries(libc_fflush_contract_tests common fmt::fmt)
target_include_directories(libc_fflush_contract_tests PRIVATE ${inc_headers})

add_executable(kernel_semaphore_contract_tests EXCLUDE_FROM_ALL
	../tests/KernelSemaphoreContractTests.cpp
)
target_include_directories(kernel_semaphore_contract_tests PRIVATE ${inc_headers})

add_executable(pthread_cond_contract_tests EXCLUDE_FROM_ALL
	../tests/PthreadCondContractTests.cpp
)
target_include_directories(pthread_cond_contract_tests PRIVATE ${inc_headers})

add_executable(kernel_sync_on_address_contract_tests EXCLUDE_FROM_ALL
	../tests/KernelSyncOnAddressContractTests.cpp
	kernel/syncOnAddress.cpp
)
target_include_directories(kernel_sync_on_address_contract_tests PRIVATE ${inc_headers})

add_executable(pm4_packet_bounds_tests EXCLUDE_FROM_ALL
	../tests/Pm4PacketBoundsTests.cpp
)
target_include_directories(pm4_packet_bounds_tests PRIVATE ${inc_headers})

add_executable(pad_contract_tests EXCLUDE_FROM_ALL
	../tests/PadContractTests.cpp
	libs/controller.cpp
)
target_link_libraries(pad_contract_tests common fmt::fmt)
target_include_directories(pad_contract_tests PRIVATE ${inc_headers})

add_executable(video_out_contract_tests EXCLUDE_FROM_ALL
	../tests/VideoOutContractTests.cpp
)
target_link_libraries(video_out_contract_tests Vulkan::Headers)
target_include_directories(video_out_contract_tests PRIVATE ${inc_headers})

add_executable(graphics_sync_contract_tests EXCLUDE_FROM_ALL
	../tests/GraphicsSyncContractTests.cpp
)
target_include_directories(graphics_sync_contract_tests PRIVATE ${inc_headers})

add_executable(depth_metadata_contract_tests EXCLUDE_FROM_ALL
	../tests/DepthMetadataContractTests.cpp
)
target_link_libraries(depth_metadata_contract_tests Vulkan::Headers)
target_include_directories(depth_metadata_contract_tests PRIVATE ${inc_headers})

add_executable(polygon_offset_contract_tests EXCLUDE_FROM_ALL
	../tests/PolygonOffsetContractTests.cpp
)
target_include_directories(polygon_offset_contract_tests PRIVATE ${inc_headers})

add_executable(line_width_contract_tests EXCLUDE_FROM_ALL
	../tests/LineWidthContractTests.cpp
)
target_link_libraries(line_width_contract_tests Vulkan::Headers)
target_include_directories(line_width_contract_tests PRIVATE ${inc_headers})

add_executable(raster_op_contract_tests EXCLUDE_FROM_ALL
	../tests/RasterOpContractTests.cpp
)
target_link_libraries(raster_op_contract_tests Vulkan::Headers)
target_include_directories(raster_op_contract_tests PRIVATE ${inc_headers})

add_executable(primitive_restart_contract_tests EXCLUDE_FROM_ALL
	../tests/PrimitiveRestartContractTests.cpp
)
target_link_libraries(primitive_restart_contract_tests Vulkan::Headers)
target_include_directories(primitive_restart_contract_tests PRIVATE ${inc_headers})

add_executable(provoking_vertex_contract_tests EXCLUDE_FROM_ALL
	../tests/ProvokingVertexContractTests.cpp
)
target_link_libraries(provoking_vertex_contract_tests Vulkan::Headers)
target_include_directories(provoking_vertex_contract_tests PRIVATE ${inc_headers})

add_executable(vertex_window_offset_contract_tests EXCLUDE_FROM_ALL
	../tests/VertexWindowOffsetContractTests.cpp
)
target_link_libraries(vertex_window_offset_contract_tests Vulkan::Headers)
target_include_directories(vertex_window_offset_contract_tests PRIVATE ${inc_headers})

add_executable(perspective_correction_contract_tests EXCLUDE_FROM_ALL
	../tests/PerspectiveCorrectionContractTests.cpp
)
target_link_libraries(perspective_correction_contract_tests Vulkan::Headers)
target_include_directories(perspective_correction_contract_tests PRIVATE ${inc_headers})

add_executable(polygon_mode_contract_tests EXCLUDE_FROM_ALL
	../tests/PolygonModeContractTests.cpp
)
target_link_libraries(polygon_mode_contract_tests Vulkan::Headers)
target_include_directories(polygon_mode_contract_tests PRIVATE ${inc_headers})

add_executable(conservative_rasterization_contract_tests EXCLUDE_FROM_ALL
	../tests/ConservativeRasterizationContractTests.cpp
)
target_link_libraries(conservative_rasterization_contract_tests Vulkan::Headers)
target_include_directories(conservative_rasterization_contract_tests PRIVATE ${inc_headers})

add_executable(force_dest_alpha_contract_tests EXCLUDE_FROM_ALL
	../tests/ForceDestAlphaContractTests.cpp
)
target_link_libraries(force_dest_alpha_contract_tests Vulkan::Headers)
target_include_directories(force_dest_alpha_contract_tests PRIVATE ${inc_headers})

add_executable(ps_sample_rate_contract_tests EXCLUDE_FROM_ALL
	../tests/PsSampleRateContractTests.cpp
)
target_link_libraries(ps_sample_rate_contract_tests Vulkan::Headers)
target_include_directories(ps_sample_rate_contract_tests PRIVATE ${inc_headers})

add_executable(video_dec2_contract_tests EXCLUDE_FROM_ALL
	../tests/VideoDec2ContractTests.cpp
)
target_include_directories(video_dec2_contract_tests PRIVATE ${inc_headers})

add_executable(sysmodule_contract_tests EXCLUDE_FROM_ALL
	../tests/SysmoduleContractTests.cpp
	libs/libSysmodule.cpp
	loader/symbolDatabase.cpp
)
target_link_libraries(sysmodule_contract_tests common fmt::fmt)
target_include_directories(sysmodule_contract_tests PRIVATE ${inc_headers})

add_executable(app_content_contract_tests EXCLUDE_FROM_ALL
	../tests/AppContentContractTests.cpp
	libs/libAppContent.cpp
	loader/symbolDatabase.cpp
)
target_link_libraries(app_content_contract_tests common fmt::fmt)
target_include_directories(app_content_contract_tests PRIVATE ${inc_headers})

add_executable(playgo_lifecycle_contract_tests EXCLUDE_FROM_ALL
	../tests/PlayGoLifecycleContractTests.cpp
	libs/libPlayGo.cpp
	loader/symbolDatabase.cpp
)
target_link_libraries(playgo_lifecycle_contract_tests common fmt::fmt)
target_include_directories(playgo_lifecycle_contract_tests PRIVATE ${inc_headers})

add_executable(png_dec_contract_tests EXCLUDE_FROM_ALL
	../tests/PngDecContractTests.cpp
	libs/libPngDec.cpp
	loader/symbolDatabase.cpp
)
target_link_libraries(png_dec_contract_tests common fmt::fmt)
target_include_directories(png_dec_contract_tests PRIVATE ${inc_headers})

add_executable(user_service_contract_tests EXCLUDE_FROM_ALL
	../tests/UserServiceContractTests.cpp
	libs/libUserService.cpp
	loader/symbolDatabase.cpp
)
target_link_libraries(user_service_contract_tests common fmt::fmt)
target_include_directories(user_service_contract_tests PRIVATE ${inc_headers})

add_executable(loader_elf_contract_tests EXCLUDE_FROM_ALL
	../tests/LoaderElfContractTests.cpp
	loader/elf.cpp
)
target_link_libraries(loader_elf_contract_tests common fmt::fmt)
target_include_directories(loader_elf_contract_tests PRIVATE ${inc_headers})

add_executable(common_dialog_contract_tests EXCLUDE_FROM_ALL
	../tests/CommonDialogContractTests.cpp
	libs/libDialog.cpp
	libs/dialog.cpp
	libs/imeDialog.cpp
	loader/symbolDatabase.cpp
)
target_link_libraries(common_dialog_contract_tests common fmt::fmt)
target_include_directories(common_dialog_contract_tests PRIVATE ${inc_headers})

add_executable(runtime_linker_bounds_tests EXCLUDE_FROM_ALL
	../tests/RuntimeLinkerBoundsTests.cpp
)
target_include_directories(runtime_linker_bounds_tests PRIVATE ${inc_headers})

add_executable(image_page_table_tests EXCLUDE_FROM_ALL
	../tests/ImagePageTableTests.cpp
)
target_link_libraries(image_page_table_tests common fmt::fmt)
target_include_directories(image_page_table_tests PRIVATE ${inc_headers})

add_executable(gpu_tile_layout_tests EXCLUDE_FROM_ALL
	../tests/GpuTileLayoutTests.cpp
	graphics/guest_gpu/gpu_format.cpp
	graphics/guest_gpu/tile.cpp
	graphics/host_gpu/renderer/image/textureCommon.cpp
	graphics/host_gpu/vulkanCommon.cpp
)
target_link_libraries(gpu_tile_layout_tests common fmt::fmt Vulkan::Headers)
target_include_directories(gpu_tile_layout_tests PRIVATE ${inc_headers})

add_kyty_full_emulator_test(shader_recompiler_compute_tests ../tests/ShaderRecompilerComputeTests.cpp)

set(gpu_test_generated_dir "${PROJECT_BINARY_DIR}/gpu_test_shaders")
set(gpu_test_ms_depth_source
	"${gpu_tiler_shader_dir}/gpu_test_ms_depth.comp")
set(gpu_test_ms_depth_spv
	"${gpu_test_generated_dir}/gpu_test_ms_depth.spv")
set(gpu_test_ms_depth_header
	"${gpu_test_generated_dir}/gpu_test_ms_depth_spv.h")
add_custom_command(
	OUTPUT "${gpu_test_ms_depth_header}"
	COMMAND ${CMAKE_COMMAND} -E make_directory "${gpu_test_generated_dir}"
	COMMAND "${KYTY_GLSLANG_VALIDATOR}" -V --target-env vulkan1.0 -Os
	        -o "${gpu_test_ms_depth_spv}" "${gpu_test_ms_depth_source}"
	COMMAND ${CMAKE_COMMAND} -DINPUT=${gpu_test_ms_depth_spv}
	        -DOUTPUT=${gpu_test_ms_depth_header} -DSYMBOL=GPU_TEST_MS_DEPTH_SPV
	        -P "${CMAKE_CURRENT_SOURCE_DIR}/embed_spirv.cmake"
	DEPENDS "${gpu_test_ms_depth_source}"
	VERBATIM
)
target_sources(shader_recompiler_compute_tests PRIVATE
	"${gpu_test_ms_depth_header}")

add_kyty_full_emulator_test(virtual_memory_allocation_tests ../tests/VirtualMemoryAllocationTests.cpp)
target_compile_definitions(virtual_memory_allocation_tests PRIVATE
	KYTY_VIRTUAL_MEMORY_ALLOCATION_TESTS=1)

# These tests use exceptions.
if(NOT KYTY_CLANG_CL)
	foreach(kyty_exception_test scalar_provenance_tests resource_tracking_tests
			virtual_memory_allocation_tests)
		target_compile_options(${kyty_exception_test} PRIVATE -fexceptions)
	endforeach()
endif()

if(BUILD_TESTING)
	add_test(NAME shader_cfg COMMAND $<TARGET_FILE:shader_cfg_tests>)
	add_test(NAME cli_options COMMAND $<TARGET_FILE:cli_options_tests>)
	add_test(NAME scalar_provenance COMMAND $<TARGET_FILE:scalar_provenance_tests>)
	add_test(NAME image_page_table COMMAND $<TARGET_FILE:image_page_table_tests>)
	add_test(NAME gpu_tile_layout COMMAND $<TARGET_FILE:gpu_tile_layout_tests>)
	add_test(NAME memory_tracker COMMAND $<TARGET_FILE:memory_tracker_tests>)
	add_test(NAME page_manager COMMAND $<TARGET_FILE:page_manager_tests>)
	add_test(NAME bit_array COMMAND $<TARGET_FILE:bit_array_tests>)
	add_test(NAME shader_vertex_metadata COMMAND $<TARGET_FILE:shader_vertex_metadata_tests>)
	add_test(NAME shader_stage_runtime COMMAND $<TARGET_FILE:shader_stage_runtime_tests>)
	add_test(NAME resource_tracking COMMAND $<TARGET_FILE:resource_tracking_tests>)
	add_test(NAME resource_mutex COMMAND $<TARGET_FILE:resource_mutex_tests>)
	add_test(NAME native_primitive_replay COMMAND $<TARGET_FILE:native_primitive_replay_tests>)
	add_test(NAME color_metadata_state COMMAND $<TARGET_FILE:color_metadata_state_tests>)
	add_test(NAME event_queue_lifetime COMMAND $<TARGET_FILE:event_queue_lifetime_tests>)
	add_test(NAME event_queue_trigger_contract
		COMMAND $<TARGET_FILE:event_queue_trigger_contract_tests>)
	add_test(NAME hle_contract COMMAND $<TARGET_FILE:hle_contract_tests>)
	add_test(NAME guest_printf_contract COMMAND $<TARGET_FILE:guest_printf_contract_tests>)
	add_test(NAME libc_fflush_contract COMMAND $<TARGET_FILE:libc_fflush_contract_tests>)
	add_test(NAME kernel_semaphore_contract
		COMMAND $<TARGET_FILE:kernel_semaphore_contract_tests>)
	add_test(NAME pthread_cond_contract
		COMMAND $<TARGET_FILE:pthread_cond_contract_tests>)
	add_test(NAME kernel_sync_on_address_contract
		COMMAND $<TARGET_FILE:kernel_sync_on_address_contract_tests>)
	add_test(NAME pm4_packet_bounds COMMAND $<TARGET_FILE:pm4_packet_bounds_tests>)
	add_test(NAME pad_contract COMMAND $<TARGET_FILE:pad_contract_tests>)
	add_test(NAME video_out_contract COMMAND $<TARGET_FILE:video_out_contract_tests>)
	add_test(NAME graphics_sync_contract COMMAND $<TARGET_FILE:graphics_sync_contract_tests>)
	add_test(NAME depth_metadata_contract COMMAND $<TARGET_FILE:depth_metadata_contract_tests>)
	add_test(NAME polygon_offset_contract COMMAND $<TARGET_FILE:polygon_offset_contract_tests>)
	add_test(NAME raster_op_contract COMMAND $<TARGET_FILE:raster_op_contract_tests>)
	add_test(NAME primitive_restart_contract COMMAND $<TARGET_FILE:primitive_restart_contract_tests>)
	add_test(NAME provoking_vertex_contract COMMAND $<TARGET_FILE:provoking_vertex_contract_tests>)
	add_test(NAME vertex_window_offset_contract
		COMMAND $<TARGET_FILE:vertex_window_offset_contract_tests>)
	add_test(NAME perspective_correction_contract
		COMMAND $<TARGET_FILE:perspective_correction_contract_tests>)
	add_test(NAME polygon_mode_contract COMMAND $<TARGET_FILE:polygon_mode_contract_tests>)
	add_test(NAME conservative_rasterization_contract
		COMMAND $<TARGET_FILE:conservative_rasterization_contract_tests>)
	add_test(NAME video_dec2_contract COMMAND $<TARGET_FILE:video_dec2_contract_tests>)
	add_test(NAME sysmodule_contract COMMAND $<TARGET_FILE:sysmodule_contract_tests>)
	add_test(NAME app_content_contract COMMAND $<TARGET_FILE:app_content_contract_tests>)
	add_test(NAME playgo_lifecycle_contract
		COMMAND $<TARGET_FILE:playgo_lifecycle_contract_tests>)
	add_test(NAME png_dec_contract COMMAND $<TARGET_FILE:png_dec_contract_tests>)
	add_test(NAME user_service_contract COMMAND $<TARGET_FILE:user_service_contract_tests>)
	add_test(NAME loader_elf_contract COMMAND $<TARGET_FILE:loader_elf_contract_tests>)
	add_test(NAME common_dialog_contract COMMAND $<TARGET_FILE:common_dialog_contract_tests>)
	add_test(NAME runtime_linker_bounds COMMAND $<TARGET_FILE:runtime_linker_bounds_tests>)
	add_test(NAME audio_out2_port COMMAND $<TARGET_FILE:audio_out2_port_tests>)
	add_test(NAME audio_out_primary_contract
		COMMAND $<TARGET_FILE:audio_out_primary_contract_tests>)
	add_test(NAME av_player_init_contract COMMAND $<TARGET_FILE:av_player_init_contract_tests>)
	add_test(NAME ajm_init_contract COMMAND $<TARGET_FILE:ajm_init_contract_tests>)
	add_test(NAME shader_recompiler_compute COMMAND $<TARGET_FILE:shader_recompiler_compute_tests>)
	add_test(NAME virtual_memory_allocation
		COMMAND $<TARGET_FILE:virtual_memory_allocation_tests>)
	add_test(NAME command_scheduler_timeline
		COMMAND $<TARGET_FILE:shader_recompiler_compute_tests> --scheduler-only)
	add_test(NAME stream_buffer_ring
		COMMAND $<TARGET_FILE:shader_recompiler_compute_tests> --stream-buffer-only)
	add_test(NAME gpu_command_lane
		COMMAND $<TARGET_FILE:shader_recompiler_compute_tests> --gpu-command-lane-only)
	add_test(NAME gpu_tiler
		COMMAND $<TARGET_FILE:shader_recompiler_compute_tests> --gpu-tiler-only)

	add_test(NAME texture_cache_layered_image
		COMMAND $<TARGET_FILE:shader_recompiler_compute_tests> --layered-image-only)
	add_test(NAME texture_cache_image_views
		COMMAND $<TARGET_FILE:shader_recompiler_compute_tests> --image-view-cache-only)
	add_test(NAME texture_cache_storage_sampled
		COMMAND $<TARGET_FILE:shader_recompiler_compute_tests> --storage-sampled-only)
	add_test(NAME texture_cache_depth_readback
		COMMAND $<TARGET_FILE:shader_recompiler_compute_tests> --depth-readback-only)
	add_test(NAME buffer_cache_dirty_gc
		COMMAND $<TARGET_FILE:shader_recompiler_compute_tests> --buffer-cache-gc-only)

	if(WIN32)
		# These tests still depend on the Windows multisample-depth path.
		add_test(NAME texture_cache_image_overlap
			COMMAND $<TARGET_FILE:shader_recompiler_compute_tests> --image-overlap-only)
		add_test(NAME texture_cache_htile_clear
			COMMAND $<TARGET_FILE:shader_recompiler_compute_tests> --htile-clear-only)
		add_test(NAME buffer_cache_ranges
			COMMAND $<TARGET_FILE:shader_recompiler_compute_tests> --buffer-cache-range-only)
	endif()

	add_custom_target(kyty_tests DEPENDS
		shader_cfg_tests
		cli_options_tests
		scalar_provenance_tests
		image_page_table_tests
		gpu_tile_layout_tests
		memory_tracker_tests
		page_manager_tests
		bit_array_tests
		shader_vertex_metadata_tests
		shader_stage_runtime_tests
		resource_tracking_tests
		resource_mutex_tests
		native_primitive_replay_tests
		color_metadata_state_tests
		event_queue_lifetime_tests
		event_queue_trigger_contract_tests
		hle_contract_tests
		kernel_semaphore_contract_tests
		pthread_cond_contract_tests
		kernel_sync_on_address_contract_tests
		pm4_packet_bounds_tests
		pad_contract_tests
		video_out_contract_tests
		graphics_sync_contract_tests
		depth_metadata_contract_tests
		polygon_offset_contract_tests
		raster_op_contract_tests
		primitive_restart_contract_tests
		provoking_vertex_contract_tests
		vertex_window_offset_contract_tests
		perspective_correction_contract_tests
		polygon_mode_contract_tests
		conservative_rasterization_contract_tests
		video_dec2_contract_tests
		sysmodule_contract_tests
		app_content_contract_tests
		playgo_lifecycle_contract_tests
		png_dec_contract_tests
		user_service_contract_tests
		loader_elf_contract_tests
		common_dialog_contract_tests
		runtime_linker_bounds_tests
		audio_out2_port_tests
		audio_out_primary_contract_tests
		av_player_init_contract_tests
		ajm_init_contract_tests
		shader_recompiler_compute_tests
		virtual_memory_allocation_tests
	)
	add_custom_target(prosperismo_tests DEPENDS kyty_tests)
endif()

set(prosperismo_platform_resources)
if(WIN32)
	list(APPEND prosperismo_platform_resources prosperismo.rc)
endif()

add_executable(prosperismo_emulator main.cpp ${kyty_emulator_src} ${prosperismo_platform_resources})
configure_macos_guest_address_space(prosperismo_emulator)

target_link_libraries(prosperismo_emulator ${kyty_emulator_link_libraries})
if (WIN32)
	target_link_libraries(prosperismo_emulator opengl32)
	target_link_libraries(prosperismo_emulator iphlpapi)
	target_link_libraries(prosperismo_emulator setupapi)
	target_link_libraries(prosperismo_emulator ws2_32)
	# onecore supplies VirtualAlloc2 / MapViewOfFile3 / UnmapViewOfFile2 and bcrypt supplies
	# BCryptGenRandom. These were previously linked only under clang-cl, so the MinGW build failed
	# to link with undefined references to all six.
	target_link_libraries(prosperismo_emulator onecore)
	target_link_libraries(prosperismo_emulator bcrypt)
endif()
if (CLANG AND NOT KYTY_CLANG_CL)
	target_link_libraries(prosperismo_emulator pthread)
endif()
# dlopen/dlsym/dladdr for RenderDoc.
target_link_libraries(prosperismo_emulator ${CMAKE_DL_LIBS})
target_include_directories(prosperismo_emulator PRIVATE ${inc_headers})

clang_tidy_check(prosperismo_emulator "" "${check_headers}" "${inc_headers}")

include_what_you_use(prosperismo_emulator "${inc_headers}")

set(KYTY_EMULATOR_MAP_NAME "prosperismo_emulator_${KYTY_COMPILER_ID}_${KYTY_LINKER_ID}.map")

set(KYTY_EMULATOR_MAP_LINK_PATH "${CMAKE_CURRENT_BINARY_DIR}/${KYTY_EMULATOR_MAP_NAME}")
set(KYTY_EMULATOR_PDB_LINK_PATH "${CMAKE_CURRENT_BINARY_DIR}/prosperismo_emulator.pdb")

if(KYTY_CLANG_CL)
	target_link_options(prosperismo_emulator PRIVATE
		"/DYNAMICBASE:NO"
		"/DEBUG:FULL"
		"/PDB:${KYTY_EMULATOR_PDB_LINK_PATH}"
		"/lldmap:${KYTY_EMULATOR_MAP_LINK_PATH}"
	)
	add_custom_command(TARGET prosperismo_emulator POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different "${KYTY_THIRD_PARTY_DIR}/winpthread/bin/libwinpthread-1.dll" $<TARGET_FILE_DIR:prosperismo_emulator>/libwinpthread-1.dll)
elseif(WIN32 OR LINUX)
	set_target_properties(prosperismo_emulator PROPERTIES LINK_FLAGS "${KYTY_LD_OPTIONS} -Wl,-Map=${KYTY_EMULATOR_MAP_LINK_PATH}")
endif()

add_dependencies(prosperismo_emulator KytyGitVersion)

if(APPLE)
	# The emulator writes x86-64 trampolines/PLT stubs into memory and executes them, and
	# runs guest code under Rosetta. That requires the JIT / unsigned-executable-memory
	# entitlements, so re-sign the binary after every link (an unsigned/relinked binary
	# reverts to the hardened defaults and aborts when it first executes written code).
	add_custom_command(TARGET prosperismo_emulator POST_BUILD
		COMMAND codesign -s - --force --options runtime
			--entitlements "${CMAKE_CURRENT_SOURCE_DIR}/macos_jit.entitlements"
			$<TARGET_FILE:prosperismo_emulator>
		COMMENT "Codesign prosperismo_emulator with JIT entitlements (macOS)")
endif()

install(TARGETS prosperismo_emulator DESTINATION .)
if(KYTY_BUILD_LAUNCHER)
	add_subdirectory(launcher)
endif()
if(KYTY_CLANG_CL)
	install(FILES "${KYTY_THIRD_PARTY_DIR}/winpthread/bin/libwinpthread-1.dll" DESTINATION .)
endif()
