using System;
using System.Collections.Generic;
using System.Linq;
using Beautiful.Core.Notifications;
using Xunit;

/// <summary>
/// 通知历史与文本清洗测试（Phase 4-T5）。
/// <para>
/// 这里钉住四件在真机上很难回放的事：首次同步必须是基线（不能把历史当新消息）、
/// 同 id 覆盖而不是重复、系统不再返回的旧条目要保留、上限之外丢最旧。
/// 文本侧钉住"任意应用发来的脏字符串"——换行/连续空格/超长正文都不能撑破一行。
/// </para>
/// </summary>
public class NotificationStoreTests
{
    private static NotificationItem Item(uint id, string title = "标题", string body = "正文",
        string app = "应用", int minutesAgo = 0)
        => new(id, "aumid.test", app, title, body,
            new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.FromHours(8)).AddMinutes(-minutesAgo));

    [Fact]
    public void 首次同步只建立基线不产生新到达()
    {
        var store = new NotificationStore();

        var added = store.Apply([Item(1), Item(2)]);

        Assert.Empty(added);
        Assert.True(store.Primed);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void 基线之后的新id才算新到达()
    {
        var store = new NotificationStore();
        store.Apply([Item(1)]);

        var added = store.Apply([Item(1), Item(2), Item(3)]);

        Assert.Equal([2u, 3u], added.Select(item => item.Id).OrderBy(id => id).ToArray());
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void 同id再次出现时覆盖内容且不重复也不当新到达()
    {
        var store = new NotificationStore();
        store.Apply([Item(1, title: "旧标题")]);

        var added = store.Apply([Item(1, title: "新标题")]);

        Assert.Empty(added);
        Assert.Single(store.Items);
        Assert.Equal("新标题", store.Items[0].Title);
    }

    [Fact]
    public void 系统不再返回的旧条目仍留在历史里()
    {
        var store = new NotificationStore();
        store.Apply([Item(1), Item(2)]);

        store.Apply([Item(2)]);   // 系统里第 1 条已被别的程序清掉

        Assert.Equal(2, store.Count);
        Assert.Contains(store.Items, item => item.Id == 1);
    }

    [Fact]
    public void 最新在前且按时间与id稳定排序()
    {
        var store = new NotificationStore();
        store.Apply([Item(1, minutesAgo: 10), Item(2, minutesAgo: 0), Item(3, minutesAgo: 5)]);

        Assert.Equal([2u, 3u, 1u], store.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void 超过上限丢最旧的一条()
    {
        var store = new NotificationStore();
        var many = Enumerable.Range(1, NotificationStore.MaxItems + 5)
            .Select(i => Item((uint)i, minutesAgo: NotificationStore.MaxItems + 6 - i))   // id 越大越新
            .ToList();

        store.Apply(many);

        Assert.Equal(NotificationStore.MaxItems, store.Count);
        Assert.DoesNotContain(store.Items, item => item.Id == 1);   // 最旧
        Assert.Contains(store.Items, item => item.Id == (uint)(NotificationStore.MaxItems + 5));
    }

    [Fact]
    public void id为零的条目被忽略()
    {
        var store = new NotificationStore();
        store.Apply([Item(0), Item(5)]);

        Assert.Single(store.Items);
        Assert.Equal(5u, store.Items[0].Id);
    }

    [Fact]
    public void 移除与清空与重置基线()
    {
        var store = new NotificationStore();
        store.Apply([Item(1), Item(2)]);

        Assert.True(store.Remove(1));
        Assert.False(store.Remove(99));
        Assert.Single(store.Items);

        store.Clear();
        Assert.Empty(store.Items);

        store.ResetBaseline();
        Assert.False(store.Primed);
        Assert.Empty(store.Apply([Item(7)]));
    }

    [Fact]
    public void 应用名为空时退回AppId再退回通知()
    {
        var withId = Item(1, app: "") with { AppId = "com.example.app" };
        Assert.Equal("com.example.app", withId.DisplayApp);

        var bare = Item(1, app: "") with { AppId = "" };
        Assert.Equal("通知", bare.DisplayApp);
        Assert.Equal("通", bare.Initial);
    }

    [Fact]
    public void 没有标题时用正文首行当标题且正文不重复首行()
    {
        var item = Item(1, title: "", body: "第一行\n第二行\n第三行");

        Assert.Equal("第一行", item.Headline);
        Assert.Equal("第二行\n第三行", item.Detail);
    }

    [Fact]
    public void 有标题时正文完整保留多行()
    {
        var item = Item(1, title: "构建完成", body: "分支 main\n耗时 1m20s");

        Assert.Equal("构建完成", item.Headline);
        Assert.Equal("分支 main\n耗时 1m20s", item.Detail);
    }

    [Fact]
    public void 文本清洗折叠空白并截断()
    {
        Assert.Equal("a b c", NotificationText.Clean(" a   b\t\tc \r\n", 50));
        Assert.Equal("", NotificationText.Clean("   ", 50));
        Assert.Equal("abc…", NotificationText.Clean("abcdef", 3));
    }

    [Fact]
    public void 多行清洗保留换行但丢掉空行()
    {
        var text = NotificationText.CleanMultiline("第一行\r\n\r\n  第二行  \n", 100);
        Assert.Equal("第一行\n第二行", text);
    }

    [Fact]
    public void 相对时间在六个区间各给一句话()
    {
        var now = new DateTimeOffset(2026, 10, 30, 20, 0, 0, TimeSpan.FromHours(8));
        NotificationItem At(TimeSpan ago) => Item(1) with { CreatedAt = now - ago };

        Assert.Equal("刚刚", At(TimeSpan.FromSeconds(20)).RelativeTime(now));
        Assert.Equal("5 分钟前", At(TimeSpan.FromMinutes(5)).RelativeTime(now));
        Assert.Equal("3 小时前", At(TimeSpan.FromHours(3)).RelativeTime(now));
        Assert.Equal("昨天", At(TimeSpan.FromDays(1)).RelativeTime(now));
        Assert.Equal("10月20日", At(TimeSpan.FromDays(10)).RelativeTime(now));
        Assert.Equal("刚刚", At(TimeSpan.FromMinutes(-5)).RelativeTime(now));   // 未来时间不说负分
    }
}
