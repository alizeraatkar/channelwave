#import <Foundation/Foundation.h>
#import <GoogleCast/GoogleCast.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

static NSString *Text(const char *value) {
    return value ? [NSString stringWithUTF8String:value] : @"";
}

static char *Json(id value) {
    NSData *data = [NSJSONSerialization dataWithJSONObject:value options:0 error:NULL];
    if (!data) return NULL;
    return strdup([[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding].UTF8String);
}

static GCKCastContext *Context(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        GCKDiscoveryCriteria *criteria = [[GCKDiscoveryCriteria alloc] initWithApplicationID:kGCKDefaultMediaReceiverApplicationID];
        GCKCastOptions *options = [[GCKCastOptions alloc] initWithDiscoveryCriteria:criteria];
        options.disableDiscoveryAutostart = YES;
        options.disableAnalyticsLogging = YES;
        options.suspendSessionsWhenBackgrounded = NO;
        options.stopReceiverApplicationWhenEndingSession = YES;
        [GCKCastContext setSharedInstanceWithOptions:options];
    });
    return GCKCastContext.sharedInstance;
}

static GCKCastSession *Session(void) { return Context().sessionManager.currentCastSession; }
static GCKRemoteMediaClient *Client(void) { return Session().remoteMediaClient; }

char *cw_cast_devices(void) {
    GCKDiscoveryManager *manager = Context().discoveryManager;
    [manager startDiscovery];
    NSMutableArray *devices = [NSMutableArray array];
    for (NSUInteger index = 0; index < manager.deviceCount; index++) {
        GCKDevice *device = [manager deviceAtIndex:index];
        if (device.deviceID.length)
            [devices addObject:@{@"id": [@"cast:" stringByAppendingString:device.deviceID],
                                 @"name": device.friendlyName ?: @"Google Cast", @"remote": @YES}];
    }
    return Json(devices);
}

int cw_cast_connect(const char *identifier) {
    if (Session().connectionState == GCKConnectionStateConnected &&
        [Session().device.deviceID isEqualToString:Text(identifier)]) return 1;
    GCKDiscoveryManager *manager = Context().discoveryManager;
    for (NSUInteger index = 0; index < manager.deviceCount; index++) {
        GCKDevice *device = [manager deviceAtIndex:index];
        if ([device.deviceID isEqualToString:Text(identifier)])
            return [Context().sessionManager startSessionWithDevice:device];
    }
    return 0;
}

char *cw_cast_receiver(const char *identifier) {
    GCKCastSession *session = Session();
    if (session.connectionState != GCKConnectionStateConnected || ![session.device.deviceID isEqualToString:Text(identifier)])
        return NULL;
    return strdup(session.device.networkAddress.ipAddress.UTF8String ?: "");
}

int cw_cast_load(const char *url, const char *mime, const char *title, const char *artist, double position) {
    GCKRemoteMediaClient *client = Client();
    if (!client) return 0;
    GCKMediaMetadata *metadata = [[GCKMediaMetadata alloc] initWithMetadataType:GCKMediaMetadataTypeMusicTrack];
    [metadata setString:Text(title) forKey:kGCKMetadataKeyTitle];
    [metadata setString:Text(artist) forKey:kGCKMetadataKeyArtist];
    GCKMediaInformationBuilder *media = [[GCKMediaInformationBuilder alloc] initWithContentURL:[NSURL URLWithString:Text(url)]];
    media.contentType = Text(mime);
    media.metadata = metadata;
    media.streamType = GCKMediaStreamTypeBuffered;
    GCKMediaLoadRequestDataBuilder *load = [[GCKMediaLoadRequestDataBuilder alloc] init];
    load.mediaInformation = [media build];
    load.autoplay = @NO;
    load.startTime = position;
    return [client loadMediaWithLoadRequestData:[load build]] != nil;
}

char *cw_cast_state(const char *url) {
    if (Session().connectionState != GCKConnectionStateConnected) return NULL;
    GCKRemoteMediaClient *client = Client();
    GCKMediaStatus *status = client.mediaStatus;
    GCKMediaInformation *media = status.mediaInformation;
    if (!status || ![(media.contentURL.absoluteString ?: media.contentID) isEqualToString:Text(url)]) return NULL;
    double duration = media.streamDuration;
    if (!isfinite(duration) || duration < 0) duration = 0;
    BOOL ended = status.idleReason == GCKMediaPlayerIdleReasonFinished;
    double position = ended ? duration : [client approximateStreamPosition];
    if (!isfinite(position) || position < 0) position = 0;
    return Json(@{@"position": @(position), @"duration": @(duration), @"buffered": @(duration),
                  @"paused": @(status.playerState != GCKMediaPlayerStatePlaying), @"ended": @(ended),
                  @"volume": @(status.volume), @"muted": @(status.isMuted),
                  @"buffering": @(status.playerState == GCKMediaPlayerStateBuffering),
                  @"error": status.idleReason == GCKMediaPlayerIdleReasonError
                      ? @"The Cast device stopped playback." : NSNull.null});
}

void cw_cast_play(void) { [Client() play]; }
void cw_cast_pause(void) { [Client() pause]; }
void cw_cast_stop(void) { [Client() stop]; }
void cw_cast_seek(double position) {
    GCKMediaSeekOptions *options = [[GCKMediaSeekOptions alloc] init];
    options.interval = position;
    [Client() seekWithOptions:options];
}
void cw_cast_volume(double volume) { [Client() setStreamVolume:(float)volume]; }
void cw_cast_disconnect(void) { [Context().sessionManager endSessionAndStopCasting:YES]; }
void cw_cast_free(char *value) { free(value); }
