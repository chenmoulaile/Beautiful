namespace Beautiful.Core.Launcher;

/// <summary>
/// 汉字 → 拼音首字母表（v3 T7 启动器的中文搜索基座）。
/// <para>
/// 【为什么手写表而不是引 NuGet 包】启动器要能"jsq 搜到计算器"，这需要把汉字折算成拼音首字母。
/// 现成方案要么引全量拼音库（本仓库离线还原未必成功，且为一个匹配功能拖进几千行数据），要么读
/// GB2312 码位分区表（.NET 8 里要额外注册 CodePages 编码提供程序，同样是 NuGet）。这里只解决
/// 本项目真正遇到的那批字——**开始菜单里出现过的应用名用字**——一张手写字典即可，纯数据、可单测、
/// 零依赖。表外的汉字一律按"该位置匹配不到任何字母"处理（见 <see cref="Positions"/>），
/// 于是错表只会让某个词搜不到，绝不会搜错出另一个词。
/// </para>
/// <para>
/// 【多音字】表里少数几个字给多个候选首字母（乐 = l 或 y、重 = c 或 z……）：一个位置允许命中
/// 候选集合里的任一字母，"音乐"于是同时收得到 yy 与 yl。候选只给**常见读音**，不给姓氏/生僻读音，
/// 否则一位多字母会把排序稀释成噪声。
/// </para>
/// <para>
/// 【确定性】表在静态构造期一次性建成（只读字典），同一输入恒得同一输出 —— 启动器的排序契约
/// 要求"同样的输入同样的次序"，拼音折算必须也在这条线上。
/// </para>
/// </summary>
public static class PinyinInitials
{
    /// <summary>
    /// 按首字母分组的字表（zh/ch/sh 归入 z/c/s，与"首字母"定义一致）。
    /// 收录原则：只收<b>软件名与系统项名里出现过的常用字</b>，宁缺毋错——错一个字最多让一个词搜不到，
    /// 收一个读音不确定的字则会把它错配到别的字母上。
    /// </summary>
    private static readonly (string Initial, string Chars)[] Groups =
    [
        ("a", "安爱阿艾傲奥澳暗按案岸昂凹啊碍矮霭鞍俺"),
        ("b", "巴把吧疤白百拜败搬班般颁板版办半伴扮瓣邦帮梆榜膀绑棒磅包杯悲碑背贝备倍被辈奔本笨比笔"
             + "彼币必毕闭庇壁避边编鞭变便遍标表憋宾滨冰兵饼秉波玻拨伯泊勃博箔播薄不布步部补捕堡碧保暴宝"),
        ("c", "擦猜才材财采彩菜苍仓操曹草册测侧厕策层蹭查茶察场差超车扯臣陈晨尘趁称城成承程惩吃池迟"
             + "持尺齿赤翅充虫崇抽初除厨楚处触川穿船垂锤春纯词磁雌此次匆从丛村存醋窜错仓储"),
        ("d", "答打大呆代带待贷担当党刀导岛倒到道盗德得的地第帝递点电店垫定东冬董动冻洞都斗逗豆督毒独"
             + "读渡度杜肚端短段断对队盾多夺朵躲剁堕灯登低敌丁盯顶订钉抖"),
        ("e", "额鹅蛾儿而二饵恩尔哦"),
        ("f", "发罚乏阀法凡烦繁反返犯饭范贩方房防纺放非飞肥费分纷粉份风丰封疯逢缝凤夫服福浮父付妇附"
             + "复赴富赋腐辅抚府斧肤弗拂沸废肺符"),
        ("g", "该改盖甘杆肝赶感刚钢岗港高糕搞哥歌阁格革隔个各给跟根更工公功供共勾沟狗够购构古谷股骨"
             + "固故顾瓜刮挂关观官冠馆管光广归规柜贵桂滚棍国果裹过"),
        ("h", "孩海害含寒汉旱航好号耗喝何合河盒贺黑痕很恨红宏洪湖虎户护花华画化话怀欢环缓换幻黄皇荒"
             + "慌灰辉回慧会汇惠或货获祸活伙火惑狐壶糊沪呼忽浑混魂婚罕喊后乎涵"),
        ("j", "机鸡积基激技极级即急集几己挤计记季既家加价架假坚间简检减见建健件江姜将奖讲交郊焦教节"
             + "结解接姐介届金今斤仅进近京经精净境静警竟敬镜究九久酒旧就聚剧句卷觉决绝均军君键舰剪"
             + "煎尖兼监柬捡荐鉴箭津筋谨惊竞截夹具辑"),
        ("k", "开凯康扛考靠科克客课肯恳空孔控口扣枯哭酷库裤快款亏葵魁坤扩括刻渴颗磕炕看抗刊勘坎砍夸"),
        ("l", "垃拉来赖蓝兰览懒郎狼廊劳老姥冷厘离李里理礼力立利例连联莲练恋良粮凉量两亮辆谅疗聊料列"
             + "裂林临邻灵零龄令领另流刘留六龙隆楼漏路录陆鹿律虑滤绿乱掠略轮论罗萝螺洛落驴吕铝旅屡"             + "氯辣腊哩浏历励络"),
        ("m", "妈麻马码玛忙盲茫猫毛矛茂冒貌贸么梅枚媒眉美妹门们猛梦弥迷谜米密蜜棉免勉面苗描秒妙灭民"
             + "敏名明鸣命摸模摩魔抹末莫默墨木目墓幕慕暮母亩某每买"),
        ("n", "拿哪那纳南男难脑恼呢内能你拟逆年念娘鸟尼泥宁凝牛扭纽农浓弄女暖挪诺懦虐捏聂镍拟"),
        ("o", "欧偶藕讴"),
        ("p", "怕排派盘判旁胖跑炮陪培配盆喷朋蓬捧碰批劈皮爬片骗飘贫频品平评凭屏坡泼迫破魄普浦谱铺扑"
             + "朴葡蒲婆拍拼"),
        ("q", "七期妻骑其奇齐旗棋气汽器弃契千迁签前钱潜浅强墙抢桥瞧切且怯亲青清晴情请秋球求区曲趣全"             + "权泉拳劝确雀群圈去驱渠取启起岂穷匙"),
        ("r", "然燃让热人任认刃日荣容融柔肉如入软阮瑞锐润若弱惹扔仍忍韧冉蕊绒"),
        ("s", "三山闪善绍社设射涉舍伸身深神审生声升绳省盛胜圣师失诗施时实食使始士市事室视试是适收手"
             + "守首受售书输树数刷耍帅双水睡说丝司私死四寺送速苏俗随岁碎孙损算虽缩锁所素诉"             + "肃赛散桑扫色森杀沙傻删扇擅搜上示商"),
        ("t", "塔台抬太摊谈汤堂趟涛桃逃讨套特疼腾腿妥拓天田甜填条跳贴铁厅听停亭挺通同铜童统痛投头图"             + "途土团推退拖贪坛烫淘提题体替态"),
        ("w", "外万弯王亡网往望忘旺为维伟尾未位味温文闻纹稳问翁我沃卧悟误污屋无五午武物务雾违围唯微"             + "威慰卫妄宛汪腕玩"),
        ("y", "呀牙芽亚言眼演厌养样摇遥药要耀也野业叶页音银饮印应英鹰迎盈影映咬爷夜一衣依仪宜已以艺"
             + "忆义益逸意因阴引隐硬永用优忧尤由油游友有又右幼鱼余于与语玉育预域圆原援延严研"             + "盐颜悦阅云匀允运韵越跃约月亿亦翼译易谊倚乙蚁议远邮源元员院愿缘娱钥"),
        ("z", "杂在再造则责择贼怎增赠扎闸炸章张掌账找照者浙珍真镇振争睁征症种州周粥轴肘主住注助筑抓"
             + "专转撞装状准桌拙子自字宗总走租足族阻组钻嘴最罪尊昨左作坐座资暂枣燥摘宅展沾赵"             + "肇哲辙针侦枕疹诊震挣帜智治质致制至志指只纸执值植置兆召罩载仔知支中钟终洲祝著追直站址众浊"),
        ("x", "西希息喜洗系细虾小晓笑心新信星形型响讯迅学雪寻巡训项相香箱详想向象效校写些卸鞋协械谢下戏序虚销消醒"),
        ];

    /// <summary>
    /// 常见多音字（只收"应用名里真会出现、且两个读音的首字母不同"的）：
    /// 长/重/传/朝 = c 与 z，行 = x 与 h，调/弹 = d 与 t，卡 = k 与 q，率 = l 与 s，
    /// 系 = x 与 j，曾 = z 与 c，单 = d 与 s，泊 = b 与 p，秘 = m 与 b，乐 = l 与 y。
    /// </summary>
    private static readonly (char Char, string Candidates)[] Polyphones =
    [
        ('乐', "ly"), ('长', "cz"), ('重', "cz"), ('传', "cz"), ('朝', "cz"),
        ('行', "xh"), ('调', "dt"), ('弹', "dt"), ('卡', "kq"), ('率', "ls"),
        ('系', "xj"), ('曾', "zc"), ('单', "ds"), ('泊', "bp"), ('秘', "mb"),
    ];

    /// <summary>单个位置的候选首字母（正常为一个字母，多音字为 2 个）。
    /// 声明必须在 <see cref="Groups"/>/<see cref="Polyphones"/> <b>之后</b>：静态字段初始化按声明顺序执行，
    /// 反过来写会让 Build() 读到还没赋值的 null 数组（本仓库的"半构造"教训在静态构造期同样成立）。</summary>
    private static readonly Dictionary<char, string> Table = Build();

    /// <summary>表内条目数（诊断/单测用：改表时能一眼看到规模变化）。</summary>
    public static int TableSize => Table.Count;

    /// <summary>是否落在 CJK 统一表意文字区（基本区 + 扩展 A）。判定不含假名/全角标点。</summary>
    public static bool IsHan(char c) =>
        c is >= '一' and <= '鿿' ||   // CJK 基本区 U+4E00–U+9FFF
        c is >= '㐀' and <= '䶿';       // 扩展 A U+3400–U+4DBF

    /// <summary>单字候选首字母；非汉字或表外汉字返回 null（调用方按"这一位不可字母匹配"处理）。</summary>
    public static string? CandidatesOf(char c) => Table.TryGetValue(c, out var value) ? value : null;

    /// <summary>
    /// 把名称折算成<b>逐位置候选串序列</b>（首字母缩写序列，匹配用的唯一形态）：
    /// <list type="bullet">
    /// <item><description>一个 ASCII 字母/数字<b>词</b>只贡献它的首字母，且分隔符会重新起词
    /// （"Task Manager" → t,m；"vs code" → v,c；"7-Zip" → 7,z）。拉丁词的完整拼写由
    /// <see cref="LauncherIndex"/> 的文本命中负责，缩写这一路只负责"词首速记"。</description></item>
    /// <item><description>表内汉字 → 该字的候选首字母（多音字为两个候选），<b>每个汉字各占一位</b>
    /// （"计算器" → j,s,q）。</description></item>
    /// <item><description>表外汉字 → 保留该汉字本身：任何 ASCII 查询都对不上，于是"未知汉字"既不参与
    /// 首字母匹配，也不会让整条序列错位（同长度、同位置）。</description></item>
    /// <item><description>空白与标点（空格 / - _ ( ) 等）<b>不成位置</b>：它们只是词界。</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> Positions(string? name)
    {
        if (string.IsNullOrEmpty(name)) return [];
        var list = new List<string>(name!.Length / 2 + 2);
        var inWord = false;                     // 当前是否还在一个 ASCII 字母/数字词里（词内后续字母不再占位）
        foreach (var c in name!)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (!inWord) list.Add(char.ToLowerInvariant(c).ToString());
                inWord = true;
                continue;
            }
            inWord = false;
            if (IsHan(c)) list.Add(Table.TryGetValue(c, out var value) ? value : c.ToString());
        }
        return list;
    }

    /// <summary>
    /// 查询串是否命中首字母序列：<paramref name="query"/> 的每个字母依次落在候选序列的<b>连续</b>位置上，
    /// <paramref name="prefixOnly"/>=true 时还必须从头开始。查询里的空白先剔除——首字母序列本就没有
    /// 分隔符，带空格的查询（"j sq"）与不带（"jsq"）应当同解。
    /// </summary>
    public static bool Matches(IReadOnlyList<string> positions, string? query, bool prefixOnly)
    {
        if (positions.Count == 0 || string.IsNullOrWhiteSpace(query)) return false;
        var letters = Compact(query);
        if (letters.Length == 0) return false;
        if (prefixOnly) return MatchAt(positions, letters, 0);
        for (var start = 0; start + letters.Length <= positions.Count; start++)
            if (MatchAt(positions, letters, start)) return true;
        return false;
    }

    /// <summary>首字母匹配用的紧凑查询（小写 + 去空白）；App 侧与单测共用同一口径，不各写一份。</summary>
    public static string Compact(string query)
    {
        Span<char> buffer = stackalloc char[query.Length];
        var written = 0;
        foreach (var c in query)
        {
            if (char.IsWhiteSpace(c)) continue;
            buffer[written++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..written]);
    }

    private static bool MatchAt(IReadOnlyList<string> positions, string letters, int offset)
    {
        if (offset + letters.Length > positions.Count) return false;
        for (var i = 0; i < letters.Length; i++)
        {
            // 该位置的候选串里必须有这个字母：多音字（"ly"）由此两读皆可命中，
            // 而表外汉字（候选就是那个汉字本身）永远等不到 ASCII 字母，于是自然匹配不上。
            if (positions[offset + i].IndexOf(letters[i], StringComparison.Ordinal) < 0) return false;
        }
        return true;
    }

    // ---------- 表 ----------

    /// <summary>
    /// 建表：每组"一个首字母 + 该组汉字串"，后接多音字覆盖组（覆盖优先，故必须最后写入）。
    /// 组内顺序按拼音读起来顺，便于人工校对；重复字无害（同值覆盖）。
    /// </summary>
    private static Dictionary<char, string> Build()
    {
        var table = new Dictionary<char, string>();
        foreach (var (initial, chars) in Groups)
            foreach (var c in chars)
                table[c] = initial;
        // 多音字：候选给常见读音的首字母，多个字母写在一个串里（"ly" = l 或 y）
        foreach (var (polyphone, candidates) in Polyphones)
            table[polyphone] = candidates;
        return table;
    }


}
