#ifndef EMULATOR_INCLUDE_EMULATOR_LIBS_AVPLAYERCONTRACTS_H_
#define EMULATOR_INCLUDE_EMULATOR_LIBS_AVPLAYERCONTRACTS_H_

namespace Libs::Audio::AvPlayer {

[[nodiscard]] constexpr int VisibleStreamCount(bool streams_available,
                                               int  container_stream_count) noexcept {
	return streams_available ? container_stream_count : 0;
}

} // namespace Libs::Audio::AvPlayer

#endif /* EMULATOR_INCLUDE_EMULATOR_LIBS_AVPLAYERCONTRACTS_H_ */
