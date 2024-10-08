if(CLR_CMAKE_TARGET_OSX OR CLR_CMAKE_TARGET_FREEBSD OR CLR_CMAKE_TARGET_HAIKU)
  # On OSX and *BSD, we use the libunwind that's part of the OS
  # On Haiku, we used a special port of libunwind
  set(CLR_CMAKE_USE_SYSTEM_LIBUNWIND 1)
endif(CLR_CMAKE_TARGET_OSX OR CLR_CMAKE_TARGET_FREEBSD OR CLR_CMAKE_TARGET_HAIKU)

if(NOT DEFINED ENV{ROOTFS_DIR})
  include_directories(SYSTEM /usr/local/include)
elseif (CLR_CMAKE_TARGET_FREEBSD)
  include_directories(SYSTEM $ENV{ROOTFS_DIR}/usr/local/include)
endif()


include(configure.cmake)

project(coreclrpal)

set(CMAKE_INCLUDE_CURRENT_DIR ON)

if(CORECLR_SET_RPATH)
    # Enable @rpath support for shared libraries.
    set(MACOSX_RPATH ON)
endif(CORECLR_SET_RPATH)

cmake_policy(SET CMP0042 NEW)

# Include directories

include_directories(include)

# Compile options

if(CLR_CMAKE_HOST_ARCH_AMD64)
  set(PAL_ARCH_SOURCES_DIR amd64)
elseif(CLR_CMAKE_HOST_ARCH_ARM)
  set(PAL_ARCH_SOURCES_DIR arm)
elseif(CLR_CMAKE_HOST_ARCH_ARMV6)
  set(PAL_ARCH_SOURCES_DIR arm)
  add_definitions(-D__armv6__)
elseif(CLR_CMAKE_HOST_ARCH_ARM64)
  set(PAL_ARCH_SOURCES_DIR arm64)
elseif(CLR_CMAKE_HOST_ARCH_LOONGARCH64)
  set(PAL_ARCH_SOURCES_DIR loongarch64)
elseif(CLR_CMAKE_HOST_ARCH_RISCV64)
  set(PAL_ARCH_SOURCES_DIR riscv64)
elseif(CLR_CMAKE_HOST_ARCH_I386)
  set(PAL_ARCH_SOURCES_DIR i386)
elseif(CLR_CMAKE_HOST_ARCH_S390X)
  set(PAL_ARCH_SOURCES_DIR s390x)
elseif(CLR_CMAKE_HOST_ARCH_POWERPC64)
  set(PAL_ARCH_SOURCES_DIR ppc64le)
endif()

if(CLR_CMAKE_TARGET_OSX)
  add_definitions(-DTARGET_OSX)
  if(CLR_CMAKE_HOST_ARCH_AMD64)
    add_definitions(-DXSTATE_SUPPORTED)
  endif()
  set(PLATFORM_SOURCES
    arch/${PAL_ARCH_SOURCES_DIR}/context.S
  )
endif(CLR_CMAKE_TARGET_OSX)

if (FEATURE_ENABLE_NO_ADDRESS_SPACE_RANDOMIZATION)
  add_definitions(-DFEATURE_ENABLE_NO_ADDRESS_SPACE_RANDOMIZATION)
endif(FEATURE_ENABLE_NO_ADDRESS_SPACE_RANDOMIZATION)
add_definitions(-DLP64COMPATIBLE)
add_definitions(-DCORECLR)
add_definitions(-DPIC)

if(CLR_CMAKE_HOST_ARCH_AMD64 AND CLR_CMAKE_TARGET_LINUX AND NOT CLR_CMAKE_TARGET_LINUX_MUSL)
  # Currently the _xstate is not available on Alpine Linux
  add_definitions(-DXSTATE_SUPPORTED)
endif(CLR_CMAKE_HOST_ARCH_AMD64 AND CLR_CMAKE_TARGET_LINUX AND NOT CLR_CMAKE_TARGET_LINUX_MUSL)

if(CLR_CMAKE_HOST_ARCH_ARM64 AND CLR_CMAKE_TARGET_LINUX AND NOT CLR_CMAKE_TARGET_LINUX_MUSL)
  # Currently the _xstate is not available on Alpine Linux
  add_definitions(-DXSTATE_SUPPORTED)
endif(CLR_CMAKE_HOST_ARCH_ARM64 AND CLR_CMAKE_TARGET_LINUX AND NOT CLR_CMAKE_TARGET_LINUX_MUSL)

if(CLR_CMAKE_TARGET_LINUX_MUSL)
  # Setting RLIMIT_NOFILE breaks debugging of coreclr on Alpine Linux for some reason
  add_definitions(-DDONT_SET_RLIMIT_NOFILE)
  # On Alpine Linux, we need to ensure that the reported stack range for the primary thread is
  # larger than the initial committed stack size.
  add_definitions(-DENSURE_PRIMARY_STACK_SIZE)
endif(CLR_CMAKE_TARGET_LINUX_MUSL)

if(CLR_CMAKE_TARGET_HAIKU)
  add_definitions(-D_GNU_SOURCE)
endif(CLR_CMAKE_TARGET_HAIKU)

# turn off capability to remove unused functions (which was enabled in debug build with sanitizers)
set(CMAKE_SHARED_LINKER_FLAGS_DEBUG "${CMAKE_SHARED_LINKER_FLAGS_DEBUG} -Wl,--no-gc-sections")

set(ARCH_SOURCES
  arch/${PAL_ARCH_SOURCES_DIR}/debugbreak.S
)
if(CLR_CMAKE_HOST_ARCH_ARM)
  if (CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    set_source_files_properties(exception/seh.cpp PROPERTIES COMPILE_FLAGS -Wno-error=inline-asm)
  endif()
endif(CLR_CMAKE_HOST_ARCH_ARM)

if (CMAKE_CXX_COMPILER_ID MATCHES "GNU" AND (CLR_CMAKE_HOST_ARCH_AMD64 OR CLR_CMAKE_HOST_ARCH_I386))
  add_compile_options(-Wa,--divide)
endif()

set(SOURCES
  cruntime/wchar.cpp
  debug/debug.cpp
  file/directory.cpp
  file/file.cpp
  file/filetime.cpp
  file/find.cpp
  file/path.cpp
  handlemgr/handleapi.cpp
  handlemgr/handlemgr.cpp
  init/pal.cpp
  init/sxs.cpp
  loader/module.cpp
  locale/unicode.cpp
  ${CLR_SRC_NATIVE_DIR}/minipal/utf8.c
  ${CLR_SRC_NATIVE_DIR}/minipal/unicodedata.c
  map/common.cpp
  map/map.cpp
  map/virtual.cpp
  misc/dbgmsg.cpp
  misc/environ.cpp
  misc/error.cpp
  misc/errorstrings.cpp
  misc/fmtmessage.cpp
  misc/miscpalapi.cpp
  misc/strutil.cpp
  misc/sysinfo.cpp
  misc/time.cpp
  misc/utils.cpp
  objmgr/palobjbase.cpp
  objmgr/shmobject.cpp
  objmgr/shmobjectmanager.cpp
  safecrt/makepath_s.cpp
  safecrt/memcpy_s.cpp
  safecrt/memmove_s.cpp
  safecrt/mbusafecrt.cpp
  safecrt/safecrt_input_s.cpp
  safecrt/safecrt_output_l.cpp
  safecrt/safecrt_output_s.cpp
  safecrt/safecrt_winput_s.cpp
  safecrt/safecrt_woutput_s.cpp
  safecrt/splitpath_s.cpp
  safecrt/sprintf_s.cpp
  safecrt/sscanf_s.cpp
  safecrt/strcat_s.cpp
  safecrt/strcpy_s.cpp
  safecrt/strncat_s.cpp
  safecrt/strncpy_s.cpp
  safecrt/swprintf.cpp
  safecrt/vsprintf.cpp
  safecrt/vswprint.cpp
  safecrt/wcscat_s.cpp
  safecrt/wcscpy_s.cpp
  safecrt/wcslen_s.cpp
  safecrt/wcsncat_s.cpp
  safecrt/wcsncpy_s.cpp
  safecrt/wmakepath_s.cpp
  safecrt/xtoa_s.cpp
  safecrt/xtow_s.cpp
  shmemory/shmemory.cpp
  sync/cs.cpp
  synchobj/mutex.cpp
  synchmgr/synchcontrollers.cpp
  synchmgr/synchmanager.cpp
  synchmgr/wait.cpp
  thread/process.cpp
  thread/thread.cpp
  thread/threadsusp.cpp
)

add_library(coreclrpal
  STATIC
  ${SOURCES}
  ${ARCH_SOURCES}
  ${PLATFORM_SOURCES}
)

if(CLR_CMAKE_TARGET_OSX)
  find_library(COREFOUNDATION CoreFoundation)
  find_library(CORESERVICES CoreServices)
  find_library(SECURITY Security)
  find_library(SYSTEM System)
  target_link_libraries(coreclrpal
    PUBLIC
    ${COREFOUNDATION}
    ${CORESERVICES}
    ${SECURITY}
    ${SYSTEM}
  )
endif(CLR_CMAKE_TARGET_OSX)

if(CLR_CMAKE_TARGET_FREEBSD)
  target_link_libraries(coreclrpal
    PUBLIC
    pthread
    rt
  )
endif(CLR_CMAKE_TARGET_FREEBSD)

if(CLR_CMAKE_TARGET_HAIKU)
  target_link_libraries(coreclrpal
    PUBLIC
    bsd
  )
endif(CLR_CMAKE_TARGET_HAIKU)

if(CLR_CMAKE_TARGET_LINUX)
  # On Android, we don't need to link with gcc_s, pthread and rt
  if(NOT CLR_CMAKE_TARGET_ANDROID)
    target_link_libraries(coreclrpal
      PUBLIC
      gcc_s
      pthread
      rt
    )
  else(NOT CLR_CMAKE_TARGET_ANDROID)
    target_link_libraries(coreclrpal
      PUBLIC
      ${ANDROID_GLOB}
      ${LZMA})
  endif(NOT CLR_CMAKE_TARGET_ANDROID)

  target_link_libraries(coreclrpal
    PUBLIC
    dl
  )

endif(CLR_CMAKE_TARGET_LINUX)
if(CLR_CMAKE_TARGET_NETBSD)
  add_definitions(-D_KMEMUSER)
  find_library(KVM kvm)
  target_link_libraries(coreclrpal
    PUBLIC
    pthread
    rt
    ${KVM}
  )
endif(CLR_CMAKE_TARGET_NETBSD)
if(CLR_CMAKE_TARGET_SUNOS)
  target_link_libraries(coreclrpal
    PUBLIC
    pthread
    rt
  )
endif(CLR_CMAKE_TARGET_SUNOS)
