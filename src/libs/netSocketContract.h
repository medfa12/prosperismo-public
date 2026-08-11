#ifndef LIBS_NET_SOCKET_CONTRACT_H_INCLUDED
#define LIBS_NET_SOCKET_CONTRACT_H_INCLUDED

namespace Libs::Network::Net {

enum class HostSocketAction {
	CreateIpv4Datagram,
	UnsupportedFamily,
	UnsupportedType,
	UnsupportedProtocol,
};

enum class HostSocketOption {
	Unsupported,
	ReceiveBuffer,
	Linger,
	SendBuffer,
	Broadcast,
	Ipv4HeaderIncluded,
};

[[nodiscard]] constexpr HostSocketAction ResolveHostSocketAction(int family, int type,
                                                                 int protocol) {
	if (family != 2) {
		return HostSocketAction::UnsupportedFamily;
	}
	if (type != 2) {
		return HostSocketAction::UnsupportedType;
	}
	if (protocol != 0) {
		return HostSocketAction::UnsupportedProtocol;
	}
	return HostSocketAction::CreateIpv4Datagram;
}

[[nodiscard]] constexpr HostSocketOption ResolveHostSocketOption(int level, int option) {
	if (level == 0xffff) {
		switch (option) {
			case 0x1002: return HostSocketOption::ReceiveBuffer;
			case 0x0080: return HostSocketOption::Linger;
			case 0x1001: return HostSocketOption::SendBuffer;
			case 0x0020: return HostSocketOption::Broadcast;
			default: return HostSocketOption::Unsupported;
		}
	}
	if (level == 0 && option == 2) {
		return HostSocketOption::Ipv4HeaderIncluded;
	}
	return HostSocketOption::Unsupported;
}

[[nodiscard]] constexpr bool HostSocketMessageFlagsSupported(int flags) {
	return flags == 0;
}

} // namespace Libs::Network::Net

#endif // LIBS_NET_SOCKET_CONTRACT_H_INCLUDED
