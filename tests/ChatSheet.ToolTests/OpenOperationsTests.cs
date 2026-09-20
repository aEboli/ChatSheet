using System;
using ChatSheet.AddIn.Tools;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    internal static class OpenOperationsTests
    {
        internal static void Run(ToolExecutor executor, Action<string, bool, string> report)
        {
            Func<string, ToolResult> run = json => executor.Execute("excel_object", JObject.Parse(json));
            Action<string, string> succeeds = (name, json) => {
                var result = run(json); report(name, result.Ok, result.Error);
            };
            succeeds("对象入口写入二维数据", "{root:'worksheet',path:[{member:'Range',args:['J1:K3']}],action:'set',member:'Value2',args:[{matrix:[['类','值'],['甲',2],['乙',3]]}]}");
            var read = executor.Execute("read_range", JObject.Parse("{range:'J1:K3'}"));
            report("对象写入读回一致", JObject.FromObject(read.Data)["values"][2][1].Value<int>() == 3, null);
            succeeds("设置边框", "{root:'worksheet',path:[{member:'Range',args:['J1:K3']},{member:'Borders'}],action:'set',member:'LineStyle',args:[1]}");
            var border = run("{root:'worksheet',path:[{member:'Range',args:['J1:K3']},{member:'Borders'},{member:'Item',args:[7]}],action:'get',member:'LineStyle'}");
            report("边框读回一致", border.Ok && JObject.FromObject(border.Data)["value"].Value<int>() == 1, border.Error);
            succeeds("筛选", "{root:'worksheet',path:[{member:'Range',args:['J1:K3']}],action:'call',member:'AutoFilter',args:[1,'甲']}");
            var filtered = run("{root:'worksheet',path:[],action:'get',member:'FilterMode'}");
            report("筛选确实生效", filtered.Ok && JObject.FromObject(filtered.Data)["value"].Value<bool>(), filtered.Error);
            succeeds("清除筛选", "{root:'worksheet',path:[],action:'call',member:'ShowAllData'}");
            succeeds("添加数据验证", "{root:'worksheet',path:[{member:'Range',args:['M1']},{member:'Validation'}],action:'call',member:'Add',args:[3,1,1,'甲,乙']}");
            var validation = run("{root:'worksheet',path:[{member:'Range',args:['M1']},{member:'Validation'}],action:'get',member:'Formula1'}");
            report("验证规则读回一致", validation.Ok && JObject.FromObject(validation.Data)["value"].Value<string>() == "甲,乙", validation.Error);
            var copy = run("{root:'worksheet',path:[{member:'Range',args:['J1:K3']}],action:'call',member:'Copy',args:[{ref:{root:'worksheet',path:[{member:'Range',args:['O1']}]}}]}");
            var copied = executor.Execute("read_range", JObject.Parse("{range:'P3'}"));
            report("复制如实反馈宿主结果", copy.Ok
                ? copied.Ok && JObject.FromObject(copied.Data)["values"][0][0].Value<int?>() == 3
                : copy.ErrorCode == "HOST_ERROR", copy.Error);
            if (!copy.Ok) { Console.WriteLine("  宿主兼容性限制：" + copy.Error); }
            succeeds("准备插入验证数据", "{root:'worksheet',path:[{member:'Range',args:['O1:P3']}],action:'set',member:'Value2',args:[{matrix:[['类','值'],['甲',2],['乙',3]]}]}");
            succeeds("插入单元格", "{root:'worksheet',path:[{member:'Range',args:['O2:P2']}],action:'call',member:'Insert',args:[-4121]}");
            var shifted = executor.Execute("read_range", JObject.Parse("{range:'P4'}"));
            report("插入后的数据已移动", shifted.Ok && JObject.FromObject(shifted.Data)["values"][0][0].Value<int?>() == 3, shifted.Error);
            succeeds("不连续区域格式", "{root:'worksheet',path:[{member:'Range',args:['R1,T1']},{member:'Font'}],action:'set',member:'Bold',args:[true]}");
            var union = run("{root:'worksheet',path:[{member:'Range',args:['T1']},{member:'Font'}],action:'get',member:'Bold'}");
            report("第二个不连续区域也被修改", union.Ok && JObject.FromObject(union.Data)["value"].Value<bool>(), union.Error);
            succeeds("创建条件格式", "{root:'worksheet',path:[{member:'Range',args:['K2:K3']},{member:'FormatConditions'}],action:'call',member:'Add',args:[1,5,'1']}");
            var conditions = run("{root:'worksheet',path:[{member:'Range',args:['K2:K3']},{member:'FormatConditions'}],action:'get',member:'Count'}");
            report("条件格式读回一致", conditions.Ok && JObject.FromObject(conditions.Data)["value"].Value<int>() == 1, conditions.Error);
            succeeds("创建命名区域", "{root:'workbook',path:[{member:'Names'}],action:'call',member:'Add',args:['OpenOpsName','=Sheet1!$J$1:$K$3']}");
            var name = run("{root:'workbook',path:[{member:'Names'},{member:'Item',kind:'call',args:['OpenOpsName']}],action:'get',member:'RefersTo'}");
            report("命名区域读回一致", name.Ok && JObject.FromObject(name.Data)["value"].Value<string>().Contains("$J$1:$K$3"), name.Error);
            succeeds("设置行隐藏", "{root:'worksheet',path:[{member:'Rows',args:['20:21']}],action:'set',member:'Hidden',args:[true]}");
            var hidden = run("{root:'worksheet',path:[{member:'Rows',args:['21:21']}],action:'get',member:'Hidden'}");
            report("行隐藏读回一致", hidden.Ok && JObject.FromObject(hidden.Data)["value"].Value<bool>(), hidden.Error);
            succeeds("设置页面方向", "{root:'worksheet',path:[{member:'PageSetup'}],action:'set',member:'Orientation',args:[2]}");
            var orientation = run("{root:'worksheet',path:[{member:'PageSetup'}],action:'get',member:'Orientation'}");
            report("页面方向读回一致", orientation.Ok && JObject.FromObject(orientation.Data)["value"].Value<int>() == 2, orientation.Error);
            succeeds("设置冻结分割行", "{root:'window',path:[],action:'set',member:'SplitRow',args:[1]}");
            succeeds("冻结窗格", "{root:'window',path:[],action:'set',member:'FreezePanes',args:[true]}");
            var frozen = run("{root:'window',path:[],action:'get',member:'FreezePanes'}");
            report("冻结读回一致", frozen.Ok && JObject.FromObject(frozen.Data)["value"].Value<bool>(), frozen.Error);
            succeeds("创建透视缓存和透视表", "{root:'workbook',path:[{member:'PivotCaches',kind:'call'},{member:'Create',kind:'call',args:[1,'Sheet1!R1C10:R3C11']}],action:'call',member:'CreatePivotTable',args:['Sheet1!R10C10','OpenOpsPivot']}");
            succeeds("配置透视字段", "{root:'worksheet',path:[{member:'PivotTables',args:['OpenOpsPivot']},{member:'PivotFields',args:['类']}],action:'set',member:'Orientation',args:[1]}");
            var pivot = run("{root:'worksheet',path:[{member:'PivotTables',args:['OpenOpsPivot']},{member:'PivotFields',args:['类']}],action:'get',member:'Orientation'}");
            report("透视字段读回一致", pivot.Ok && JObject.FromObject(pivot.Data)["value"].Value<int>() == 1, pivot.Error);
            var invalid = run("{root:'worksheet',path:[],action:'call',member:'NotARealExcelMethod'}");
            report("无效成员返回失败", !invalid.Ok, invalid.Error);
            var page1 = executor.Execute("read_range", JObject.Parse("{range:'A1:A5001'}"));
            var page2 = executor.Execute("read_range", JObject.Parse("{range:'A1:A5001',offset:5000}"));
            report("5001 格分页完整覆盖", page1.Ok && page2.Ok &&
                JObject.FromObject(page1.Data)["next_offset"].Value<int>() == 5000 &&
                JObject.FromObject(page2.Data)["rows"].Value<int>() == 1 &&
                JObject.FromObject(page2.Data)["next_offset"].Type == JTokenType.Null, null);
            var wide = executor.Execute("read_range", JObject.Parse("{range:'A1:XFD2',offset:15000}"));
            report("宽范围尾页不越过行边界", wide.Ok && JObject.FromObject(wide.Data)["columns"].Value<int>() == 1384 &&
                JObject.FromObject(wide.Data)["next_offset"].Value<int>() == 16384, wide.Error);
            var matrix = new JArray();
            for (var i = 0; i < 5001; i++) { matrix.Add(new JArray(i)); }
            var writeArgs = new JObject { ["range"] = "Z1:Z5001", ["values"] = matrix };
            var written = executor.Execute("write_values", writeArgs);
            var tail = executor.Execute("read_range", JObject.Parse("{range:'Z5001'}"));
            report("分块写入覆盖末格", written.Ok && tail.Ok && JObject.FromObject(tail.Data)["values"][0][0].Value<int>() == 5000, written.Error);
            executor.Execute("clear_range", JObject.Parse("{range:'Z1:Z5001',scope:'contents'}"));
            var chunks = 0;
            executor.BeforeWriteChunk = () => { if (++chunks == 2) { throw new OperationCanceledException("测试取消"); } };
            ToolResult partial;
            try { partial = executor.Execute("write_values", writeArgs); }
            finally { executor.BeforeWriteChunk = null; }
            var unwritten = executor.Execute("read_range", JObject.Parse("{range:'Z5001'}"));
            report("分块中断返回实际进度且尾格未写入", !partial.Ok && partial.ErrorCode == "WRITE_PARTIAL" &&
                JObject.FromObject(partial.Data)["cells_written"].Value<int>() == 5000 &&
                JObject.FromObject(unwritten.Data)["values"][0][0].Type == JTokenType.Null, partial.Error);
        }
    }
}
