#include "../native/bridge.c"
int main(void) {
    const wchar_t *valid=JSB_SESSION_ROOT L"0123456789abcdef0123456789abcdef\\bin\\JSAssistantBridge.dll";
    if(!own_payload_path(valid)) return 1;
    if(own_payload_path(L"G:\\development\\enjoy\\jiangshen-assistant\\research\\unity-object-layer\\bridge\\sessions\\0123456789abcdef0123456789abcdef\\bin\\JSAssistantBridge.dll")) return 2;
    if(own_payload_path(JSB_SESSION_ROOT L"0123456789abcdef0123456789abcdeF\\bin\\JSAssistantBridge.dll")) return 3;
    if(own_payload_path(JSB_SESSION_ROOT L"0123456789abcdef0123456789abcdef\\bin\\Other.dll")) return 4;
    if(own_payload_path(JSB_SESSION_ROOT L"0123456789abcdef0123456789abcdef\\..\\JSAssistantBridge.dll")) return 5;
    if(own_payload_path(JSB_SESSION_ROOT L"0123456789abcdef0123456789abcdef\\bin\\JSAssistantBridge.dll.extra")) return 6;
    puts("{\"status\":\"PASS\",\"scope\":\"production native immutable-cache path whitelist\",\"checks\":6}"); return 0;
}
