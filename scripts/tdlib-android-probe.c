// Emulator smoke check: verifies the pinned runtime and a Telegram handshake through the host proxy.
// No API credentials, phone numbers, login codes, or account sessions are used.
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef void *(*create_fn)(void);
typedef void (*send_fn)(void *, const char *);
typedef const char *(*receive_fn)(void *, double);
typedef const char *(*execute_fn)(void *, const char *);
typedef void (*destroy_fn)(void *);

int main(int argc, char **argv) {
    if (argc < 2 || argc > 5) { fprintf(stderr, "Usage: tdlib-emulator-probe /path/to/libtdjson.so [http|socks5] [proxy-server] [dc-id]\n"); return 2; }
    void *library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!library) { fprintf(stderr, "%s\n", dlerror()); return 2; }
    create_fn create = (create_fn)dlsym(library, "td_json_client_create");
    send_fn send = (send_fn)dlsym(library, "td_json_client_send");
    receive_fn receive = (receive_fn)dlsym(library, "td_json_client_receive");
    execute_fn execute = (execute_fn)dlsym(library, "td_json_client_execute");
    destroy_fn destroy = (destroy_fn)dlsym(library, "td_json_client_destroy");
    if (!create || !send || !receive || !execute || !destroy) { fprintf(stderr, "Missing JSON API export\n"); return 2; }
    execute(NULL, "{\"@type\":\"setLogStream\",\"log_stream\":{\"@type\":\"logStreamEmpty\"}}");
    execute(NULL, "{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":0}");
    void *client = create();
    if (!client) return 2;
    send(client, "{\"@type\":\"getOption\",\"name\":\"version\",\"@extra\":\"version\"}");
    send(client, "{\"@type\":\"getOption\",\"name\":\"commit_hash\",\"@extra\":\"commit\"}");
    const char *proxy_type = argc >= 3 && strcmp(argv[2], "socks5") == 0 ? "proxyTypeSocks5" : "proxyTypeHttp";
    const char *server = argc >= 4 ? argv[3] : "127.0.0.1";
    int dc_id = argc >= 5 ? atoi(argv[4]) : 2;
    if (dc_id < 1 || dc_id > 5 || strlen(server) > 100 || strpbrk(server, "\"\\")) return 2;
    char proxy_query[512];
    snprintf(proxy_query, sizeof(proxy_query), "{\"@type\":\"testProxy\",\"proxy\":{\"@type\":\"proxy\",\"server\":\"%s\",\"port\":10808,\"type\":{\"@type\":\"%s\",\"username\":\"\",\"password\":\"\",\"http_only\":false}},\"dc_id\":%d,\"timeout\":15,\"@extra\":\"telegramHandshake\"}", server, proxy_type, dc_id);
    send(client, proxy_query);
    int replies = 0, failed = 0;
    time_t deadline = time(NULL) + 30;
    while (replies < 3 && time(NULL) < deadline) {
        const char *response = receive(client, 0.1);
        if (!response || !strstr(response, "\"@extra\"")) continue;
        puts(response);
        fflush(stdout);
        replies++;
        if (strstr(response, "\"@type\":\"error\"")) failed = 1;
        if (strstr(response, "\"@extra\":\"commit\"") && !strstr(response, "c15d3f5a5de6e3ba5839822c451152e5e18bb700")) failed = 1;
        if (strstr(response, "\"@extra\":\"version\"") && !strstr(response, "1.8.68")) failed = 1;
    }
    destroy(client);
    dlclose(library);
    return replies == 3 && !failed ? 0 : 1;
}
