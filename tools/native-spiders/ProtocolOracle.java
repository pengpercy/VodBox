import java.nio.file.*;
import java.lang.reflect.*;
import org.json.*;

/** Optional analysis tool; uses the existing JavaHost (JDK 21), never the app's C# path.
 * Arguments: input.json output.json. Input contains NativeProbe settings plus requests[].
 */
public final class ProtocolOracle {
    public static void main(String[] args) throws Exception {
        System.setOut(System.err);
        java.security.Security.addProvider(new org.bouncycastle.jce.provider.BouncyCastleProvider());
        JSONObject input = new JSONObject(Files.readString(Path.of(args[0])));
        var session = tvbox.runtime.NativeProbe.initialize(input);
        Field spiderField = session.getClass().getDeclaredField("spider"); spiderField.setAccessible(true);
        var actual = (com.github.catvod.crawler.Spider)spiderField.get(session);
        // The native codec encrypts controlled plaintext after the loader is initialized.
        // Thus fixture input need not depend on a second implementation of the codec.
        if (input.has("plainExt")) actual.init(com.github.catvod.spider.Init.application,
            com.github.catvod.spider.DexNative.encrypt(input.getString("plainExt")));
        // Explicit test-only field overrides point the original protocol at local fixtures.
        if (input.has("fields")) {
            JSONObject fields = input.getJSONObject("fields");
            for (java.util.Iterator<?> names = fields.keys(); names.hasNext();) {
                String name = (String) names.next();
                Field field = actual.getClass().getDeclaredField(name); field.setAccessible(true);
                field.set(actual, fields.getString(name));
            }
        }
        var results = new JSONArray();
        JSONArray requests = input.getJSONArray("requests");
        for (int index = 0; index < requests.length(); index++) {
            JSONObject request = requests.getJSONObject(index);
            JSONObject result = new JSONObject().put("request", request);
            try { result.put("response", new JSONObject(session.request(request))); }
            catch (Throwable error) { result.put("error", error.toString()); }
            results.put(result);
        }
        JSONObject output = new JSONObject().put("results", results);
        if (input.has("tingInputs")) {
            JSONArray vectors = new JSONArray();
            JSONArray values = input.getJSONArray("tingInputs");
            for (int index = 0; index < values.length(); index++) {
                String value = values.getString(index);
                vectors.put(new JSONObject().put("input", value)
                    .put("output", com.github.catvod.spider.DexNative.native_ting_md5(value)));
            }
            output.put("tingVectors", vectors);
        }
        if (input.has("playDetail")) {
            String id = input.getString("playDetail");
            JSONObject detail = new JSONObject(session.request(new JSONObject().put("ids", id))).getJSONArray("list").getJSONObject(0);
            String[] flags = detail.getString("vod_play_from").split("\\$\\$\\$", -1);
            String[] groups = detail.getString("vod_play_url").split("\\$\\$\\$", -1);
            for (int index = 0; index < groups.length; index++) {
                String first = groups[index].split("#", -1)[0];
                String url = first.substring(first.indexOf('$') + 1);
                JSONObject request = new JSONObject().put("detailID", id).put("flag", flags[index]).put("play", url);
                JSONObject result = new JSONObject().put("request", request);
                try { result.put("response", new JSONObject(session.request(request))); }
                catch (Throwable error) { result.put("error", error.toString()); }
                results.put(result);
            }
        }
        if (input.has("signInput")) {
            Object spider = actual;
            Class<?> owner = spider.getClass();
            while (owner != null) {
                try {
                    Method method = owner.getDeclaredMethod("yq", String.class); method.setAccessible(true);
                    output.put("sign", method.invoke(spider, input.getString("signInput")));
                    break;
                } catch (NoSuchMethodException absent) { owner = owner.getSuperclass(); }
            }
        }
        Files.writeString(Path.of(args[1]), output.toString(2));
        System.err.println("PROTOCOL_ORACLE_DONE");
        System.exit(0);
    }
}
