﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HMQService.Common;
using System.Threading;
using HMQService.Decode;
using HMQService.Database;
using HMQService.Model;
using System.Data;

namespace HMQService.Server
{
    public class DataHandler
    {
        private string m_data = string.Empty;
        private Thread m_dataHandlerThread = null;
        private Dictionary<int, CarManager> m_dicCars = new Dictionary<int, CarManager>();
        private Dictionary<string, CameraConf> m_dicCameras = new Dictionary<string, CameraConf>();
        private Dictionary<string, JudgementRule> m_dicJudgeRules = new Dictionary<string, JudgementRule>();
        private Dictionary<int, ExamProcedure> m_dicExamProcedures = new Dictionary<int, ExamProcedure>();
        private IDataProvider m_sqlDataProvider = null;

        public DataHandler(Byte[] data, int nSize, Dictionary<int, CarManager> dicCars, Dictionary<string, CameraConf> dicCameras,
            Dictionary<string, JudgementRule> dicRules, Dictionary<int, ExamProcedure> dicExamProcedures, 
            IDataProvider sqlDataProvider)
        {
            m_data = Encoding.ASCII.GetString(data, 0, nSize);
            m_dicCars = dicCars;
            m_dicCameras = dicCameras;
            m_dicJudgeRules = dicRules;
            m_dicExamProcedures = dicExamProcedures;
            m_sqlDataProvider = sqlDataProvider;

            Log.GetLogger().InfoFormat("接收到车载数据：{0}", m_data);
        }

        ~DataHandler()
        {
            StopHandle();
        }

        public void StartHandle()
        {
            if (!string.IsNullOrEmpty(m_data))
            {
                m_dataHandlerThread =
                    new Thread(new ThreadStart(DataHandlerThreadProc));

                m_dataHandlerThread.Start();
            }
        }

        public void StopHandle()
        {
            // Wait for one second for the the thread to stop.
            m_dataHandlerThread.Join(1000);

            // If still alive; Get rid of the thread.
            if (m_dataHandlerThread.IsAlive)
            {
                m_dataHandlerThread.Abort();
            }
            m_dataHandlerThread = null;
        }

        private void DataHandlerThreadProc()
        {
            string errorMsg = string.Empty;

            //解析车载数据
            string[] retArray = BaseMethod.SplitString(m_data, BaseDefine.SPLIT_CHAR_ASTERISK, out errorMsg);
            if (!string.IsNullOrEmpty(errorMsg))
            {
                goto END;
            }

            int nLength = retArray.Length;
            if (nLength != BaseDefine.INTERFACE_FIELD_COUNT_KM2
                && nLength != BaseDefine.INTERFACE_FIELD_COUNT_KM3)
            {
                errorMsg = string.Format("车载数据格式不正确，{0} 数量不对", BaseDefine.SPLIT_CHAR_ASTERISK);
                goto END;
            }

            string strKch = retArray[1];
            int nKch = string.IsNullOrEmpty(strKch) ? 0 : int.Parse(strKch);    //考车号
            string strType = retArray[2];
            int nType = string.IsNullOrEmpty(strType) ? 0 : int.Parse(strType); //类型
            string strXmbh = retArray[5];   //项目编号
            string strZkzh = retArray[6];   //准考证号
            string strTime = retArray[7];   //时间

            if (!m_dicCars.ContainsKey(nKch))
            {
                Log.GetLogger().ErrorFormat("找不到考车{0}，请检查配置", nKch);
                return;
            }
            Log.GetLogger().InfoFormat(
                "接收到车载接口信息，考车号={0}, 类型={1}, 项目编号={2}, 准考证号={3}, 时间={4}",
                nKch, nType, strXmbh, strZkzh, strTime);

            switch (nType)
            {
                case BaseDefine.PACK_TYPE_M17C51:   //考试开始
                    {
                        HandleM17C51(nKch, strZkzh);
                    }
                    break;
                case BaseDefine.PACK_TYPE_M17C52:   //项目开始
                    {
                        HandleM17C52(nKch, strZkzh, strXmbh);
                    }
                    break;
                case BaseDefine.PACK_TYPE_M17C53:   //扣分
                    {
                        HandleM17C53(nKch, strXmbh, strTime);
                    }
                    break;
                case BaseDefine.PACK_TYPE_M17C54:
                    {
                        //项目抓拍照片，这里不需要处理
                    }
                    break;
                case BaseDefine.PACK_TYPE_M17C55:   //项目完成
                    {
                        HandleM17C55(nKch, strZkzh, strXmbh);
                    }
                    break;
                case BaseDefine.PACK_TYPE_M17C56:   //考试完成
                    {
                        string score = retArray[5]; //17C56时该字段为考试成绩
                        int kscj = string.IsNullOrEmpty(score) ? 0 : int.Parse(score);

                        Log.GetLogger().InfoFormat("车载传过来的考试成绩为：{0}", kscj);
                        
                        if (kscj < 0)
                        {
                            kscj = 0;
                        }

                        HandleM17C56(nKch, kscj);
                    }
                    break;
                default:
                    break;
            }

        END:
            {
                if (!string.IsNullOrEmpty(errorMsg))
                {
                    Log.GetLogger().ErrorFormat("处理车载数据(DataHandlerThreadProc)时产生错误，{0}", errorMsg);
                }
                else
                {
                    Log.GetLogger().InfoFormat("DataHandlerThreadProc 执行结束");
                }
            }

            return;
        }

        /// <summary>
        /// 考试开始
        /// </summary>
        /// <param name="kch">考车号</param>
        /// <param name="zkzmbh">准考证明编号</param>
        /// <returns></returns>
        private bool HandleM17C51(int kch, string zkzmbh)
        {
            int kscs = 0;   //考试次数
            int drcs = 0;   //当日次数
            //if (!GetExamCount(zkzmbh, ref kscs, ref drcs))
            //{
            //    return false;
            //}

            if (!m_dicExamProcedures.ContainsKey(kch))
            {
                Log.GetLogger().ErrorFormat("m_dicExamProcedures 字典找不到考车号 : {0}", kch);
                return false;
            }
            ExamProcedure examProcedure = m_dicExamProcedures[kch];

            //获取考生信息
            StudentInfo studentInfo = new StudentInfo();
            if (!GetStudentInfo(zkzmbh, ref studentInfo))
            {
                return false;
            }

            if (!examProcedure.Handle17C51(studentInfo))
            {
                Log.GetLogger().ErrorFormat("examProcedure.Handle17C51 failed, kch={0}", kch);
                return false;
            }

            //try
            //{
            //    BaseMethod.TF17C51(kch, zkzmbh, kscs, drcs);
            //}
            //catch (Exception e)
            //{
            //    Log.GetLogger().ErrorFormat("TF17C51 catch an error : {0}, kch = {1}, zkzmbh = {2}, kscs = {3}, drcs = {4}",
            //        e.Message, kch, zkzmbh, kscs, drcs);
            //    return false;
            //}

            Log.GetLogger().InfoFormat("HandleM17C51 end, kch={0}, zkzmbh={1}, kscs={2}, drcs={3}", kch, zkzmbh, kscs, drcs);
            return true;
        }

        /// <summary>
        /// 项目开始
        /// </summary>
        /// <param name="kch">考车号</param>
        /// <param name="zkzmbh">准考证明编号</param>
        /// <param name="xmbh">项目编号</param>
        /// <returns></returns>
        private bool HandleM17C52(int kch, string zkzmbh, string xmbh)
        {
            int xmCode = string.IsNullOrEmpty(xmbh) ? 0 : int.Parse(xmbh);
            int nWnd2 = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_DISPLAY, BaseDefine.CONFIG_SECTION_CONFIG,
                BaseDefine.CONFIG_KEY_WND2, 1);    //画面二状态
            Log.GetLogger().DebugFormat("nWnd2 = {0}", nWnd2);

            if (1 == nWnd2)     //值为1时进行项目动态切换
            {
                string key = string.Format("{0}_1", xmbh);
                Log.GetLogger().DebugFormat("camera key = {0}", key);
                if (!m_dicCameras.ContainsKey(key))
                {
                    Log.GetLogger().ErrorFormat("摄像头 {0} 未配置，请检查配置文件。", key);
                    return false;
                }

                CameraConf camera = m_dicCameras[key];
                m_dicCars[kch].StartDynamicDecode(camera, 1);   //第二画面进项目

                ////处理定点
                ////半坡停车时，车载会发 15010 摄像头编号过来，切换摄像头后，另外开一个线程，休眠几秒时间后，切换为原来的摄像头
                //if ((BaseDefine.XMBH_15010 == xmCode) || (BaseDefine.XMBH_15020 == xmCode) || (BaseDefine.XMBH_15030 == xmCode))
                //{
                //    Log.GetLogger().InfoFormat("定点：{0}", xmCode);

                //    if (BaseMethod.IsExistFile(BaseDefine.CONFIG_FILE_PATH_ZZIPChannel))
                //    {
                //        XmInfo xmInfo = new XmInfo(kch, xmCode);

                //        Thread QHThread = new Thread(new ParameterizedThreadStart(QHThreadProc));
                //        QHThread.Start(xmInfo);
                //    }

                //    return true;
                //}
            }

            //项目编号转换，科目二专用，数据库升级后可以不需要这段代码
            int xmCodeNew = GetKM2NewXmBh(xmCode);
            Log.GetLogger().DebugFormat("xmCodeNew = {0}", xmCodeNew);

            //获取项目类型（因为数据库里扣分类型和项目类型存在同一张表，所以这里参考C++代码，全部存在同一张字典里）
            if (!m_dicJudgeRules.ContainsKey(xmbh))
            {
                Log.GetLogger().ErrorFormat("扣分类型 {0} 未配置，请检查配置", xmbh);
                return false;
            }
            string xmlx = m_dicJudgeRules[xmbh].JudgementType;  //项目类型

            if (!m_dicExamProcedures.ContainsKey(kch))
            {
                Log.GetLogger().ErrorFormat("m_dicExamProcedures 字典找不到考车号 : {0}", kch);
                return false;
            }
            ExamProcedure examProcedure = m_dicExamProcedures[kch];

            if (!examProcedure.Handle17C52(xmCodeNew, xmlx))
            {
                Log.GetLogger().ErrorFormat("examProcedure.Handle17C52 failed, kch={0}", kch);
                return false;
            }

            //try
            //{
            //    //使用 C++ dll 进行绘制
            //    Log.GetLogger().DebugFormat("kch={0}, zkzmbh={1}, xmCode={2}, kflx={3}", kch, zkzmbh, xmCodeNew, kflx);
            //    BaseMethod.TF17C52(kch, zkzmbh, xmCodeNew, kflx);
            //}
            //catch (Exception e)
            //{
            //    Log.GetLogger().ErrorFormat("TF17C52 catch an error : {0}, kch = {1}, zkzmbh = {2}, xmCodeNew = {3}, kflx = {4}",
            //        e.Message, kch, zkzmbh, xmCodeNew, kflx);
            //    return false;
            //}

            Log.GetLogger().InfoFormat("HandleM17C52 end, kch={0}, zkzmbh={1}", kch, zkzmbh);
            return true;
        }

        private bool HandleM17C52(int kch, int bh)
        {
            string key = string.Format("{0}_{1}", kch, bh);
            if (!m_dicCameras.ContainsKey(key))
            {
                Log.GetLogger().ErrorFormat("摄像头配置 {0} 不存在，请检查配置", key);
                return false;
            }

            CameraConf camera = m_dicCameras[key];
            if (!m_dicCars[kch].StartDynamicDecode(camera, 1))
            {
                Log.GetLogger().ErrorFormat("HandleM17C52 画面切换失败，{0}", key);
                return false;
            }

            Log.GetLogger().InfoFormat("HandleM17C52 画面切换，{0}", key);
            return true;
        }

        private void QHThreadProc(object obj)
        {
            XmInfo xmInfo = (XmInfo)obj;

            int kch = xmInfo.Kch;
            int xmCode = xmInfo.XmCode;

            string section = string.Format("{0}{1}", BaseDefine.CONFIG_SECTION_Q, xmCode);
            int sleepTime = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_ZZIPChannel, section,
                BaseDefine.CONFIG_KEY_TIME, 2000);

            System.Threading.Thread.Sleep(sleepTime);

            HandleM17C52(kch, 1);

        }

        /// <summary>
        /// 项目扣分
        /// </summary>
        /// <param name="kch">考车号</param>
        /// <param name="xmbh">项目编号，包含项目编号和错误编号，用@分隔</param>
        /// <param name="time">时间</param>
        /// <returns></returns>
        private bool HandleM17C53(int kch, string xmbh, string time)
        {
            string errorMsg = string.Empty;
            string[] strArray = BaseMethod.SplitString(xmbh, BaseDefine.SPLIT_CHAR_AT, out errorMsg);
            if (!string.IsNullOrEmpty(errorMsg) || strArray.Length != 2)
            {
                Log.GetLogger().ErrorFormat("17C53 接口存在错误，{0}", errorMsg);
                return false;
            }
            string strXmCode = strArray[0];
            string strErrrorCode = strArray[1];
            int xmCode = string.IsNullOrEmpty(strXmCode) ? 0 : int.Parse(strXmCode);

            int kskm = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_ENV, BaseDefine.CONFIG_SECTION_CONFIG,
                BaseDefine.CONFIG_KEY_KSKM, 0); //考试科目
            string xmName = string.Empty;   //项目名称
            if (BaseDefine.CONFIG_VALUE_KSKM_3 == kskm) //科目三
            {
                xmName = GetKM3Name(xmCode);
            }
            else  //科目二
            {
                xmName = GetKM2Name(xmCode);
            }

            //扣分类型、扣除分数
            if (!m_dicJudgeRules.ContainsKey(strErrrorCode))
            {
                Log.GetLogger().ErrorFormat("数据库扣分规则表中不存在错误编号为 {0} 的记录，请检查配置。", strErrrorCode);
                return false;
            }
            string kflx = m_dicJudgeRules[strErrrorCode].JudgementType;
            int kcfs = m_dicJudgeRules[strErrrorCode].Points;

            if (!m_dicExamProcedures.ContainsKey(kch))
            {
                Log.GetLogger().ErrorFormat("m_dicExamProcedures 字典找不到考车号 : {0}", kch);
                return false;
            }
            ExamProcedure examProcedure = m_dicExamProcedures[kch];
            if (!examProcedure.Handle17C53(xmName, kflx, kcfs))
            {
                Log.GetLogger().ErrorFormat("examProcedure.Handle17C53 failed, kch={0}", kch);
                return false;
            }

            Log.GetLogger().InfoFormat("HandleM17C53 end, kch={0}, xmName={1}, kflx={2}, kcfs={3}", kch, xmName, kflx, kcfs);
            return true;
        }

        //根据项目编号，获取科目三项目名称
        private string GetKM3Name(int xmCode)
        {
            string xmName = string.Empty;
            switch (xmCode)
            {
                case BaseDefine.XMBH_201:
                    xmName = BaseDefine.XMMC_SCZB;  //上车准备
                    break;
                case BaseDefine.XMBH_202:
                    xmName = BaseDefine.XMMC_QB;  //起步
                    break;
                case BaseDefine.XMBH_203:
                    xmName = BaseDefine.XMMC_ZHIXIAN;   //直线
                    break;
                case BaseDefine.XMBH_204:
                    xmName = BaseDefine.XMMC_BG;    //变更
                    break;
                case BaseDefine.XMBH_205:
                    xmName = BaseDefine.XMMC_TGLK;  //通过路口
                    break;
                case BaseDefine.XMBH_206:
                    xmName = BaseDefine.XMMC_RX;    //人行
                    break;
                case BaseDefine.XMBH_207:
                    xmName = BaseDefine.XMMC_XX;    //学校
                    break;
                case BaseDefine.XMBH_208:
                    xmName = BaseDefine.XMMC_CC;    //车站
                    break;
                case BaseDefine.XMBH_209:
                    xmName = BaseDefine.XMMC_HC;    //会车
                    break;
                case BaseDefine.XMBH_210:
                    xmName = BaseDefine.XMMC_CC;    //超车
                    break;
                case BaseDefine.XMBH_211:
                    xmName = BaseDefine.XMMC_KB;    //靠边
                    break;
                case BaseDefine.XMBH_212:
                    xmName = BaseDefine.XMMC_DT;    //掉头
                    break;
                case BaseDefine.XMBH_213:
                    xmName = BaseDefine.XMMC_YJ;    //夜间
                    break;
                case BaseDefine.XMBH_214:
                    xmName = BaseDefine.XMMC_ZZ;    //左转
                    break;
                case BaseDefine.XMBH_215:
                    xmName = BaseDefine.XMMC_YZ;    //右转
                    break;
                case BaseDefine.XMBH_216:
                    xmName = BaseDefine.XMMC_ZHIXING;    //直行
                    break;
                case BaseDefine.XMBH_217:
                    xmName = BaseDefine.XMMC_JJ;    //加减
                    break;
                default:
                    xmName = BaseDefine.XMMC_ZH;    //综合
                    break;
            }

            return xmName;
        }

        //根据项目编号，获取科目二项目名称
        private string GetKM2Name(int xmCode)
        {
            string xmName = string.Empty;

            if ((xmCode > BaseDefine.XMBH_201509) && (xmCode < BaseDefine.XMBH_201700))
            {
                xmName = BaseDefine.XMMC_DCRK;  //倒车入库
            }
            else if ((xmCode > BaseDefine.XMBH_204509) && (xmCode < BaseDefine.XMBH_204700))
            {
                xmName = BaseDefine.XMMC_CFTC;  //侧方停车
            }
            else if ((xmCode > BaseDefine.XMBH_203509) && (xmCode < BaseDefine.XMBH_203700))
            {
                xmName = BaseDefine.XMMC_DDPQ;  //定点坡起
            }
            else if ((xmCode > BaseDefine.XMBH_206509) && (xmCode < BaseDefine.XMBH_206700))
            {
                xmName = BaseDefine.XMMC_QXXS;  //曲线行驶
            }
            else if ((xmCode > BaseDefine.XMBH_207509) && (xmCode < BaseDefine.XMBH_207700))
            {
                xmName = BaseDefine.XMMC_ZJZW;  //直角转弯
            }
            else if (BaseDefine.XMBH_249 == xmCode)
            {
                xmName = BaseDefine.XMMC_MNSD;  //模拟遂道
            }
            else if (BaseDefine.XMBH_259 == xmCode)
            {
                xmName = BaseDefine.XMMC_YWSH;  //雨雾湿滑
            }
            else
            {
                xmName = BaseDefine.XMMC_ZHPP;  //综合评判
            }

            return xmName;
        }

        /// <summary>
        /// 项目完成
        /// </summary>
        /// <param name="kch">考车号</param>
        /// <param name="strZkzmbh">准考证明</param>
        /// <param name="strXmbh">项目编号</param>
        /// <returns></returns>
        private bool HandleM17C55(int kch, string strZkzmbh, string strXmbh)
        {
            //项目开始编号与项目完成编号不一样，车载没有把完成编号传过来，这里需要根据开始编号进行转换
            int xmBeginCode = string.IsNullOrEmpty(strXmbh) ? 0 : int.Parse(strXmbh);
            int xmEndCode = 0;

            int kskm = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_ENV, BaseDefine.CONFIG_SECTION_CONFIG,
                BaseDefine.CONFIG_KEY_KSKM, 0); //考试科目
            if (BaseDefine.CONFIG_VALUE_KSKM_3 == kskm) //科目三
            {
                string key = string.Format("考车{0}_2", kch);
                if (!m_dicCameras.ContainsKey(key))
                {
                    Log.GetLogger().ErrorFormat("找不到 {0} 摄像头配置，请检查配置", key);
                    //return false;
                }
                else
                {
                    CameraConf camera = m_dicCameras[key];
                    m_dicCars[kch].StartDynamicDecode(camera, 1);   //车载视频动态，第二画面车外
                }

                //科目三的项目完成编号为，开始编号+700
                //218 --> 918
                if (xmBeginCode < BaseDefine.XMBH_700)
                {
                    xmEndCode = xmBeginCode + BaseDefine.XMBH_700;
                }
                else
                {
                    xmEndCode = xmBeginCode;
                }
            }
            else  //科目二
            {
                //项目编号转换，科目二专用，数据库升级后可以不需要这段代码
                xmBeginCode = GetKM2NewXmBh(xmBeginCode);

                //e.g. 201500 --> 201990
                // 201500 先除以 1000，得到 201。再乘以 1000，得到 201000。再加上 990，得到 201990。
                xmEndCode = (xmBeginCode / 1000) * 1000 + 990;

                //科目二切换到场地远景视频 
                string key = BaseDefine.STRING_KM2_PUBLIC_VIDEO;
                if (!m_dicCameras.ContainsKey(key))
                {
                    Log.GetLogger().ErrorFormat("找不到 {0} 摄像头配置，请检查配置", key);
                    //return false;
                }
                else
                {
                    CameraConf camera = m_dicCameras[key];
                    m_dicCars[kch].StartDynamicDecode(camera, 1);  
                }
            }

            //获取项目类型
            if (!m_dicJudgeRules.ContainsKey(xmEndCode.ToString()))
            {
                Log.GetLogger().ErrorFormat("ErrorData 表不存在 {0} 记录，请检查配置", xmEndCode);
                return false;
            }
            string xmlx = m_dicJudgeRules[xmEndCode.ToString()].JudgementType;

            if (!m_dicExamProcedures.ContainsKey(kch))
            {
                Log.GetLogger().ErrorFormat("m_dicExamProcedures 字典找不到考车号 : {0}", kch);
                return false;
            }
            ExamProcedure examProcedure = m_dicExamProcedures[kch];
            if (!examProcedure.Handle17C55(xmBeginCode, xmlx))
            {
                Log.GetLogger().ErrorFormat("examProcedure.Handle17C55 failed, kch={0}", kch);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 考试完成
        /// </summary>
        /// <param name="kch">考车号</param>
        /// <param name="kscj">考试成绩</param>
        /// <returns></returns>
        private bool HandleM17C56(int kch, int kscj)
        {
            int kshgfs = 0; //考试合格分数

            if (!m_dicExamProcedures.ContainsKey(kch))
            {
                Log.GetLogger().ErrorFormat("HandleM17C56错误，找不到考车 {0}", kch);
                return false;
            }
            ExamProcedure examPorcedure = m_dicExamProcedures[kch];

            int kskm = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_ENV, BaseDefine.CONFIG_SECTION_CONFIG,
                BaseDefine.CONFIG_KEY_KSKM, 0); //考试科目
            if (BaseDefine.CONFIG_VALUE_KSKM_3 == kskm) //科目三
            {
                kshgfs = BaseDefine.CONFIG_VALUE_KSHGFS_3;
            }
            else  //科目二
            {
                kshgfs = BaseDefine.CONFIG_VALUE_KSHGFS_2;
            }

            bool bPass = (kscj >= kshgfs) ? true : false;
            examPorcedure.Handle17C56(bPass);

            //try
            //{
            //    if (kscj >= kshgfs) //考试合格
            //    {
            //        BaseMethod.TF17C56(kch, 1, kscj);
            //    }
            //}
            //catch (Exception e)
            //{
            //    Log.GetLogger().ErrorFormat("TF17C56 catch an error : {0}, 考车号={1}, 科目{2}, 考试成绩={3}", e.Message,
            //        kch, kskm, kscj);
            //    return false;
            //}

            Log.GetLogger().InfoFormat("TF17C56 end, 考车号={0}, 科目{1}, 考试成绩={2}", kch, kskm, kscj);
            return true;
        }

        /// <summary>
        /// 从数据库查询考生的考试次数和当日次数
        /// </summary>
        /// <param name="zkzmbh">准考证明编号</param>
        /// <param name="kscs">考试次数</param>
        /// <param name="drcs">当日次数</param>
        /// <returns></returns>
        private bool GetExamCount(string zkzmbh, ref int kscs, ref int drcs)
        {
            kscs = -1;
            drcs = -1;

            try
            {
                string sql = string.Format("select {0},{1} from {2} where {3}='{4}'",
                    BaseDefine.DB_FIELD_KSCS, BaseDefine.DB_FIELD_DRCS, BaseDefine.DB_TABLE_STUDENTINFO,
                    BaseDefine.DB_FIELD_ZKZMBH, zkzmbh);
                DataSet ds = m_sqlDataProvider.RetriveDataSet(sql);
                if (null != ds)
                {
                    for (int i = 0; i < ds.Tables[0].Rows.Count; i++)
                    {
                        string strKscs = (null == ds.Tables[0].Rows[i][0]) ? string.Empty : ds.Tables[0].Rows[i][0].ToString();
                        string strDrcs = (null == ds.Tables[0].Rows[i][1]) ? string.Empty : ds.Tables[0].Rows[i][1].ToString();
                        kscs = string.IsNullOrEmpty(strKscs) ? 0 : int.Parse(strKscs);
                        drcs = string.IsNullOrEmpty(strDrcs) ? 0 : int.Parse(strDrcs);

                        break;
                    }
                }
            }
            catch(Exception e)
            {
                Log.GetLogger().ErrorFormat("catch an error : {0}", e.Message);
                return false;
            }

            if (-1==kscs || -1==drcs)
            {
                Log.GetLogger().ErrorFormat("从数据库 studentInfo 表获取准考证为 {0} 的考生信息失败", zkzmbh);
                return false;
            }

            Log.GetLogger().InfoFormat("考生 {0} 的考试考试次数为 {1}，当日次数为 {2}", zkzmbh, kscs, drcs);
            return true;
        }

        //科目二的项目编号有更新，这里将旧的编号转换为新的编号
        //如果现场数据库已升级到最新，则不需要调用该函数
        private int GetKM2NewXmBh(int xmCode)
        {
            int xmCodeNew = 0;
            if (xmCode > BaseDefine.XMBH_300 && xmCode < BaseDefine.XMBH_400)
            {
                xmCodeNew = BaseDefine.XMBH_201510;
            }
            else if (xmCode > BaseDefine.XMBH_400 && xmCode < BaseDefine.XMBH_500)
            {
                xmCodeNew = BaseDefine.XMBH_204510;
            }
            else if (xmCode > BaseDefine.XMBH_500 && xmCode < BaseDefine.XMBH_600)
            {
                xmCodeNew = BaseDefine.XMBH_203510;
            }
            else if (xmCode > BaseDefine.XMBH_600 && xmCode < BaseDefine.XMBH_700)
            {
                xmCodeNew = BaseDefine.XMBH_206510;
            }
            else if (xmCode > BaseDefine.XMBH_700 && xmCode < BaseDefine.XMBH_800)
            {
                xmCodeNew = BaseDefine.XMBH_207510;
            }
            else
            {
                xmCodeNew = xmCode;
            }

            return xmCodeNew;
        }

        /// <summary>
        /// 获取考生信息
        /// </summary>
        /// <param name="zkzmbh">准考证明</param>
        /// <param name="arrayZp">身份证照片信息</param>
        /// <param name="arrayMjzp">签到照片信息</param>
        /// <returns></returns>
        private bool GetStudentInfo(string zkzmbh, ref StudentInfo studentInfo)
        {
            //获取考生照片
            Byte[] arrayZp = null;  //照片
            Byte[] arrayMjzp = null;    //门禁照片，现场采集
            string sql = string.Format("select {0},{1} from {2} where {3}='{4}'",
                BaseDefine.DB_FIELD_ZP,
                BaseDefine.DB_FIELD_MJZP,
                BaseDefine.DB_TABLE_STUDENTPHOTO,
                BaseDefine.DB_FIELD_ZKZMBH,
                zkzmbh);
            Log.GetLogger().DebugFormat("获取考生照片 sql : {0}", sql);
            try
            {
                DataSet ds = m_sqlDataProvider.RetriveDataSet(sql);
                if (ds != null && ds.Tables[0] != null && ds.Tables[0].Rows != null)
                {
                    try
                    {
                        arrayZp = (Byte[])ds.Tables[0].Rows[0][0];
                    }
                    catch(Exception e)
                    {
                        Log.GetLogger().InfoFormat("照片获取失败，{0}", e.Message);
                    }

                    try
                    {
                        arrayMjzp = (Byte[])ds.Tables[0].Rows[0][1];
                    }
                    catch(Exception e)
                    {
                        Log.GetLogger().InfoFormat("门禁照片获取失败，{0}", e.Message);
                    }
                }
            }
            catch(Exception e)
            {
                Log.GetLogger().ErrorFormat("查询 StudentPhoto 表发生异常: {0}, zkzmbh={1}, sql = {2}", e.Message, zkzmbh, sql);
                //return false;
            }

            Log.GetLogger().DebugFormat("HQW TEMP");

            //获取考生信息
            string kch = string.Empty;  //考车号
            string bz = string.Empty;   //备注（车牌号）
            string kscx = string.Empty; //考试车型
            string xingming = string.Empty; //姓名
            string xb = string.Empty;   //性别
            string date = string.Empty; //日期
            string lsh = string.Empty;  //流水号
            string sfzmbh = string.Empty;   //身份证明编号
            string jxmc = string.Empty; //驾校名称
            string ksy1 = string.Empty; //考试员1
            string ksyyCode = string.Empty; //考试原因编号
            string ksyyDes = string.Empty;  //考试原因描述
            string drcs = string.Empty; //当日次数

            string sqlFormat = string.Empty;
            int dbType = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_ENV, BaseDefine.CONFIG_SECTION_CONFIG,
                BaseDefine.CONFIG_KEY_SQLORACLE, 0);
            if (1 == dbType)
            {
                sqlFormat = "select {0},{1}.{2},{3},{4},{5},(Select CONVERT(varchar(100), GETDATE(), 23)) as DATE, {6},{7},{8},{9},{10},{11},{12} from {13} left join {14} on {15}={16} left join {17} on {18}={19} where {20}='{21}'";
            }
            else
            {
                sqlFormat = "select {0},{1}.{2},{3},{4},{5},to_char(sysdate,'yyyy-mm-dd') as MYDATE, {6},{7},{8},{9},{10},{11},{12} from {13} left join {14} on {15}={16} left join {17} on {18}={19} where {20}='{21}'";
            }

            sql = string.Format(
                sqlFormat,
                BaseDefine.DB_FIELD_KCH,
                BaseDefine.DB_TABLE_SYSCFG,
                BaseDefine.DB_FIELD_BZ,
                BaseDefine.DB_FIELD_KSCX,
                BaseDefine.DB_FIELD_XINGMING,
                BaseDefine.DB_FIELD_XB,
                BaseDefine.DB_FIELD_LSH,
                BaseDefine.DB_FIELD_SFZMBH,
                BaseDefine.DB_FIELD_JXMC,
                BaseDefine.DB_FIELD_KSY1,
                BaseDefine.DB_FIELD_KSYY,
                BaseDefine.DB_FIELD_ZKZMBH,
                BaseDefine.DB_FIELD_DRCS,
                BaseDefine.DB_TABLE_STUDENTINFO,
                BaseDefine.DB_TABLE_SCHOOLINFO,
                BaseDefine.DB_FIELD_DLR,
                BaseDefine.DB_FIELD_JXBH,
                BaseDefine.DB_TABLE_SYSCFG,
                BaseDefine.DB_FIELD_KCH,
                BaseDefine.DB_FIELD_XIANGMU,
                BaseDefine.DB_FIELD_ZKZMBH,
                zkzmbh
                );
            Log.GetLogger().DebugFormat("获取考生信息 sql : {0}", sql);
            try
            {
                DataSet ds = m_sqlDataProvider.RetriveDataSet(sql);
                if (ds != null && ds.Tables[0] != null && ds.Tables[0].Rows != null)
                {
                    kch = (null == ds.Tables[0].Rows[0][0]) ? string.Empty : ds.Tables[0].Rows[0][0].ToString();
                    bz = (null == ds.Tables[0].Rows[0][1]) ? string.Empty : ds.Tables[0].Rows[0][1].ToString();
                    kscx = (null == ds.Tables[0].Rows[0][2]) ? string.Empty : ds.Tables[0].Rows[0][2].ToString();
                    xingming = (null == ds.Tables[0].Rows[0][3]) ? string.Empty : ds.Tables[0].Rows[0][3].ToString();
                    xb = (null == ds.Tables[0].Rows[0][4]) ? string.Empty : ds.Tables[0].Rows[0][4].ToString();
                    date = (null == ds.Tables[0].Rows[0][5]) ? string.Empty : ds.Tables[0].Rows[0][5].ToString();
                    lsh = (null == ds.Tables[0].Rows[0][6]) ? string.Empty : ds.Tables[0].Rows[0][6].ToString();
                    sfzmbh = (null == ds.Tables[0].Rows[0][7]) ? string.Empty : ds.Tables[0].Rows[0][7].ToString();
                    jxmc = (null == ds.Tables[0].Rows[0][8]) ? string.Empty : ds.Tables[0].Rows[0][8].ToString();
                    ksy1 = (null == ds.Tables[0].Rows[0][9]) ? string.Empty : ds.Tables[0].Rows[0][9].ToString();
                    
                    ksyyCode = (null == ds.Tables[0].Rows[0][10]) ? string.Empty : ds.Tables[0].Rows[0][10].ToString();
                    ksyyDes = getKsyy(ksyyCode);

                    drcs = (null == ds.Tables[0].Rows[0][12]) ? string.Empty : ds.Tables[0].Rows[0][12].ToString();

                    //if (string.IsNullOrEmpty(kch) || string.IsNullOrEmpty(bz) || string.IsNullOrEmpty(kscx) || string.IsNullOrEmpty(xingming)
                    //    || string.IsNullOrEmpty(xb) || string.IsNullOrEmpty(date) || string.IsNullOrEmpty(lsh) || string.IsNullOrEmpty(sfzmbh) 
                    //    || string.IsNullOrEmpty(jxmc) || string.IsNullOrEmpty(ksy1))
                    //{
                    //    Log.GetLogger().ErrorFormat("查询 StudentInfo 表值为空，sql={0}", sql);
                    //    return false;
                    //}

                    Log.GetLogger().DebugFormat("kch={0}, bz={1}, kscx={2}, xingming={3},xb={4},date={5},lsh={6},sfzmbh={7},jxmc={8},ksy1={9}, ksyyCode={10}, ksyyDes={11}, drcs={12}",
                        kch, bz, kscx, xingming, xb, date, lsh, sfzmbh, jxmc, ksy1, ksyyCode, ksyyDes, drcs);
                }
            }
            catch (Exception e)
            {
                Log.GetLogger().ErrorFormat("查询 StudentInfo 表发生异常: {0}, zkzmbh={1}", e.Message, zkzmbh);
                return false;
            }

            studentInfo = new StudentInfo(kch, bz, kscx, xingming, xb, date, lsh, sfzmbh, jxmc, ksy1, ksyyDes, drcs, arrayZp, arrayMjzp);

            Log.GetLogger().DebugFormat("GetStudentInfo success, zkzmbh={0}", zkzmbh);
            return true;
        }

        private string GetDBStringValue(DataColumn column)
        {
            string retStr = string.Empty;
            if (null == column)
            {
                return retStr;
            }


            return retStr;
        }

        private string getKsyy(string code)
        {
            string retStr = string.Empty;

            if (BaseDefine.DB_VALUE_A == code)
            {
                retStr = code + "-" + BaseDefine.DB_VALUE_CK;    //A-初考
            }
            else if (BaseDefine.DB_VALUE_B == code)
            {
                retStr = code + "-" + BaseDefine.DB_VALUE_ZJ;    //B-增驾
            }
            else if (BaseDefine.DB_VALUE_F == code)
            {
                retStr = code + "-" + BaseDefine.DB_VALUE_MFXX;    //F-满分学习
            }
            else if (BaseDefine.DB_VALUE_D == code)
            {
                retStr = code + "-" + BaseDefine.DB_VALUE_BK;    //D-补考
            }
            else
            {
                retStr = BaseDefine.DB_VALUE_KSYYWZ;    //考试原因:未知
            }

            return retStr;
        }
    }
}
