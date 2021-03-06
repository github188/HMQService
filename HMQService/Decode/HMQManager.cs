﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using HMQService.Common;
using HMQService.Database;
using HMQService.Model;
using HMQService.Server;
using System.Runtime.InteropServices;
using System.Data;
using System.Drawing;

namespace HMQService.Decode
{
    public class HMQManager
    {
        private Thread m_HMQManagerThread = null;
        private bool m_bInitSDK = false;
        private int m_userId = -1;
        private uint m_iErrorCode = 0;
        private IDataProvider sqlDataProvider = null;
        private Dictionary<string, CameraConf> dicCameras = new Dictionary<string, CameraConf>();   //摄像头信息
        private Dictionary<string, JudgementRule> dicJudgementRule = new Dictionary<string, JudgementRule>();   //扣分规则
        private Dictionary<int, CarManager> dicCars = new Dictionary<int, CarManager>();    //考车信息
        private Dictionary<int, ExamProcedure> dicExamProcedures = new Dictionary<int, ExamProcedure>();    //考试过程信息
        private int[] m_dispalyShow;
        private TCPServer tcpServer;
        private UDPServer udpServer;

        private CHCNetSDK.NET_DVR_MATRIX_ABILITY_V41 m_struDecAbility = new CHCNetSDK.NET_DVR_MATRIX_ABILITY_V41();

        public HMQManager()
        {
            m_dispalyShow = new int[5];
            
            //SDK初始化
            m_bInitSDK = CHCNetSDK.NET_DVR_Init();
            if (m_bInitSDK == false)
            {
                Log.GetLogger().ErrorFormat("SDK 初始化失败.");
                return;
            }
            else
            {
                //保存SDK日志
                CHCNetSDK.NET_DVR_SetLogToFile(3, "C:\\SdkLog\\", true);
            }
        }

        ~HMQManager()
        {
            StopWork();
        }

        public void StartWork()
        {
            m_HMQManagerThread =
                    new Thread(new ThreadStart(HMQManagerThreadProc));

            m_HMQManagerThread.Start();
        }

        public void StopWork()
        {
            if (null != tcpServer)
            {
                tcpServer.StopServer();
                tcpServer = null;
            }
            if (null != udpServer)
            {
                udpServer.StopServer();
                udpServer = null;
            }

            m_HMQManagerThread.Join(1000);

            if (m_HMQManagerThread.IsAlive)
            {
                m_HMQManagerThread.Abort();
            }
            m_HMQManagerThread = null;

            if (null != sqlDataProvider)
            {
                sqlDataProvider.Dispose();
            }
        }

        private void HMQManagerThreadProc()
        {
            Log.GetLogger().InfoFormat("HMQManagerThreadProc begin.");

            //初始化数据库
            if (!InitDatabase())
            {
                return;
            }

            //获取摄像头配置信息
            if (!GetCameraConf())
            {
                return;
            }

            //获取扣分规则
            if (!GetJudgementRule())
            {
                return;
            }

            //初始化设备和通道
            if (!InitDevices())
            {
                return;
            }

            //开始运行
            if (!RunMap())
            {
                return;
            }

            //开始监听车载数据
            tcpServer = new TCPServer(dicCars, dicCameras, dicJudgementRule, dicExamProcedures, sqlDataProvider);
            tcpServer.StartServer();
            udpServer = new UDPServer(dicCars, dicExamProcedures);
            udpServer.StartServer();

            Log.GetLogger().InfoFormat("HMQManagerThreadProc end.");
        }

        private bool LoginDevice()
        {
            string DVRIPAddress = "192.168.0.68";
            Int16 DVRPortNumber = 8000;
            string DVRUserName = "admin";
            string DVRPassword = "hk12345678";

            try
            {
                CHCNetSDK.NET_DVR_DEVICEINFO_V30 m_struDeviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
                m_userId = CHCNetSDK.NET_DVR_Login_V30(DVRIPAddress, DVRPortNumber, DVRUserName, DVRPassword, ref m_struDeviceInfo);
                if (-1 == m_userId)
                {
                    m_iErrorCode = CHCNetSDK.NET_DVR_GetLastError();
                    Log.GetLogger().ErrorFormat("NET_DVR_Login_V30 failed, error code = {0}", m_iErrorCode);
                    return false;
                }
            }
            catch(Exception e)
            {
                Log.GetLogger().InfoFormat("LoginDevice catch an error : {0}", e.Message);
                return false;
            }

            Log.GetLogger().InfoFormat("LoginDevice return {0}", m_userId.ToString());
            return true;
        }

        private bool GetSDKBuildVersion()
        {
            try
            {
                uint version = CHCNetSDK.NET_DVR_GetSDKBuildVersion();
                Log.GetLogger().InfoFormat("sdk build version : {0}", version.ToString());
            }
            catch(Exception e)
            {
                Log.GetLogger().ErrorFormat("GetSDKBuildVersion catch an error : {0}", e.Message);
                return false;
            }

            return true;
        }

        private bool InitDatabase()
        {
            //获取数据库连接串
            string connectString = GetConnectionString();
            if (string.IsNullOrEmpty(connectString))
            {
                Log.GetLogger().ErrorFormat("从配置文件获取数据库连接串失败");
                return false;
            }

            //获取数据库类型，并进行初始化连接
            int nRet = GetDatabaseType();
            if (0 == nRet)
            {
                sqlDataProvider = DataProvider.CreateDataProvider(DataProvider.DataProviderType.OracleDataProvider, connectString);
            }
            else if (1 == nRet)
            {
                sqlDataProvider = DataProvider.CreateDataProvider(DataProvider.DataProviderType.SqlDataProvider, connectString);
            }
            else
            {
                Log.GetLogger().ErrorFormat("数据库类型配置错误，SQLORORACLE = {0}", nRet.ToString());
                return false;
            }

            Log.GetLogger().InfoFormat("数据库连接成功");
            return true;
        }

        /// <summary>
        /// 读取配置文件里的数据库连接字符串
        /// 配置文件config.ini里的数据库连接字符串加过盐，这里解析时需要将每个字符“-1”后，再进行翻转
        /// </summary>
        /// <returns></returns>
        private string GetConnectionString()
        {
            string retConnectionStr = string.Empty;

            //try
            //{
            //    string configStr = INIOperator.INIGetStringValue(BaseDefine.CONFIG_FILE_PATH_CONFIG, BaseDefine.CONFIG_SECTION_SQLLINK,
            //        BaseDefine.CONFIG_KEY_ServerPZ, string.Empty);
            //    if (!string.IsNullOrEmpty(configStr))
            //    {
            //        foreach (char c in configStr.ToCharArray())
            //        {
            //            int nC = (int)c;
            //            retConnectionStr = (char)(nC - 1) + retConnectionStr;
            //        }
            //    }
            //}
            //catch(Exception e)
            //{
            //    Log.GetLogger().ErrorFormat("catch an error : {0}", e.Message);
            //}
            //Log.GetLogger().InfoFormat("connection string after decode : {0}", retConnectionStr);
            ////配置文件里的数据库连接字符串包含"Provider=SQLOLEDB;"，这在C#代码中不适用，这里去除该字段
            //if (0 == retConnectionStr.IndexOf("Provider=SQLOLEDB;"))
            //{
            //    retConnectionStr = retConnectionStr.Substring(18);
            //}
            //Log.GetLogger().InfoFormat("connection string after delete : {0}", retConnectionStr);

            try
            {
                string encodeAddress = INIOperator.INIGetStringValue(BaseDefine.CONFIG_FILE_PATH_DB,
                    BaseDefine.CONFIG_SECTION_CONFIG, BaseDefine.CONFIG_KEY_DBADDRESS, "");
                string encodeUsername = INIOperator.INIGetStringValue(BaseDefine.CONFIG_FILE_PATH_DB,
                    BaseDefine.CONFIG_SECTION_CONFIG, BaseDefine.CONFIG_KEY_USERNAME, "");
                string encodePassword = INIOperator.INIGetStringValue(BaseDefine.CONFIG_FILE_PATH_DB,
                    BaseDefine.CONFIG_SECTION_CONFIG, BaseDefine.CONFIG_KEY_PASSWORD, "");
                string encodeInstance = INIOperator.INIGetStringValue(BaseDefine.CONFIG_FILE_PATH_DB,
                    BaseDefine.CONFIG_SECTION_CONFIG, BaseDefine.CONFIG_KEY_INSTANCE, "");
                if (string.IsNullOrEmpty(encodeAddress) || string.IsNullOrEmpty(encodeUsername) || string.IsNullOrEmpty(encodePassword)
                    || string.IsNullOrEmpty(encodeInstance))
                {
                    Log.GetLogger().ErrorFormat("数据库配置存在异常");
                    return retConnectionStr;
                }

                string dbAddress = BekUtils.Util.Base64Util.Base64Decode(encodeAddress);
                string dbUsername = BekUtils.Util.Base64Util.Base64Decode(encodeUsername);
                string dbPassword = BekUtils.Util.Base64Util.Base64Decode(encodePassword);
                string dbInstance = BekUtils.Util.Base64Util.Base64Decode(encodeInstance);
                if (string.IsNullOrEmpty(dbAddress) || string.IsNullOrEmpty(dbUsername) || string.IsNullOrEmpty(dbPassword)
                    || string.IsNullOrEmpty(dbInstance))
                {
                    Log.GetLogger().ErrorFormat("数据库配置存在异常");
                    return retConnectionStr;
                }

                int dbType = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_ENV, BaseDefine.CONFIG_SECTION_CONFIG,
                    BaseDefine.CONFIG_KEY_SQLORACLE, 0);
                if (1 == dbType)
                {
                    //SQL
                    retConnectionStr = string.Format(BaseDefine.DB_CONN_FORMAT_SQL, dbAddress, dbInstance, dbUsername, dbPassword);
                }
                else
                {
                    //ORACLE
                    retConnectionStr = string.Format(BaseDefine.DB_CONN_FORMAT_ORACLE, dbUsername, dbPassword, dbAddress, dbInstance);
                }
            }
            catch(Exception e)
            {
                Log.GetLogger().ErrorFormat("catch an error : {0}", e.Message);
            }

            return retConnectionStr;
        }

        /// <summary>
        /// 根据配置文件判断数据库类型
        /// </summary>
        /// <returns>0--oracle，1--sqlserver</returns>
        private int GetDatabaseType()
        {
            int nRet = -1;

            try
            {
                string configStr = INIOperator.INIGetStringValue(BaseDefine.CONFIG_FILE_PATH_ENV, BaseDefine.CONFIG_SECTION_CONFIG,
                    BaseDefine.CONFIG_KEY_SQLORACLE, string.Empty);
                if (!string.IsNullOrEmpty(configStr))
                {
                    nRet = int.Parse(configStr);
                }
            }
            catch (Exception e)
            {
                Log.GetLogger().ErrorFormat("catch an error : {0}", e.Message);
            }

            return nRet;
        }

        /// <summary>
        /// 获取摄像头配置
        /// </summary>
        /// <returns></returns>
        private bool GetCameraConf()
        {
            dicCameras.Clear();

            string sql = string.Format("select {0},{1},{2},{3},{4},{5},{6},{7},{8} from {9} order by {10}",
                BaseDefine.DB_FIELD_BH,
                BaseDefine.DB_FIELD_SBIP,
                BaseDefine.DB_FIELD_YHM,
                BaseDefine.DB_FIELD_MM,
                BaseDefine.DB_FIELD_DKH,
                BaseDefine.DB_FIELD_TDH,
                BaseDefine.DB_FIELD_TRANSMODE,
                BaseDefine.DB_FIELD_MEDIAIP,
                BaseDefine.DB_FIELD_NID,
                BaseDefine.DB_TABLE_TBKVIDEO,
                BaseDefine.DB_FIELD_BH);

            try
            {
                DataSet ds = sqlDataProvider.RetriveDataSet(sql);
                if (null != ds)
                {
                    for(int i = 0; i < ds.Tables[0].Rows.Count; i++)
                    {
                        string bh = (null == ds.Tables[0].Rows[i][0]) ? string.Empty : ds.Tables[0].Rows[i][0].ToString();
                        string sbip = (null == ds.Tables[0].Rows[i][1]) ? string.Empty : ds.Tables[0].Rows[i][1].ToString();
                        string yhm = (null == ds.Tables[0].Rows[i][2]) ? string.Empty : ds.Tables[0].Rows[i][2].ToString();
                        string mm = (null == ds.Tables[0].Rows[i][3]) ? string.Empty : ds.Tables[0].Rows[i][3].ToString();
                        string dkh = (null == ds.Tables[0].Rows[i][4]) ? string.Empty : ds.Tables[0].Rows[i][4].ToString();
                        string tdh = (null == ds.Tables[0].Rows[i][5]) ? string.Empty : ds.Tables[0].Rows[i][5].ToString();
                        string transmode = (null == ds.Tables[0].Rows[i][6]) ? string.Empty : ds.Tables[0].Rows[i][6].ToString();
                        string mediaip = (null == ds.Tables[0].Rows[i][7]) ? string.Empty : ds.Tables[0].Rows[i][7].ToString();
                        string nid = (null == ds.Tables[0].Rows[i][8]) ? string.Empty : ds.Tables[0].Rows[i][8].ToString();

                        int iDkh = string.IsNullOrEmpty(dkh) ? 8000 : int.Parse(dkh);   //端口号
                        int iTdh = string.IsNullOrEmpty(tdh) ? -1 : int.Parse(tdh); //通道号
                        int iTransmode = string.IsNullOrEmpty(transmode) ? 1 : int.Parse(transmode);   //码流类型

                        CameraConf camera = new CameraConf(bh, sbip, yhm, mm, mediaip, iDkh, iTdh, iTransmode);

                        string key = bh + "_" + nid;
                        if (!dicCameras.ContainsKey(key))
                        {
                            dicCameras.Add(key, camera);
                        }
                    }
                }
            }
            catch(Exception e)
            {
                Log.GetLogger().ErrorFormat("catch an error : {0}", e.Message);
                return false;
            }

            if (0 == dicCameras.Count)
            {
                Log.GetLogger().ErrorFormat("初始化摄像头信息失败，数据库摄像头表解析结果为空");
                return false;
            }

            Log.GetLogger().InfoFormat("初始化摄像头信息成功");
            return true;
        }

        /// <summary>
        /// 获取扣分规则
        /// </summary>
        /// <returns></returns>
        private bool GetJudgementRule()
        {
            dicJudgementRule.Clear();

            string sql = string.Format("select {0},{1},{2} from {3}",
                BaseDefine.DB_FIELD_CWBH,
                BaseDefine.DB_FIELD_KFLX,
                BaseDefine.DB_FIELD_KCFS,
                BaseDefine.DB_TABLE_ERRORDATA);

            try
            {
                DataSet ds = sqlDataProvider.RetriveDataSet(sql);
                if (null != ds)
                {
                    for (int i = 0; i < ds.Tables[0].Rows.Count; i++)
                    {
                        string cwbh = (null == ds.Tables[0].Rows[i][0]) ? string.Empty : ds.Tables[0].Rows[i][0].ToString();
                        string kflx = (null == ds.Tables[0].Rows[i][1]) ? string.Empty : ds.Tables[0].Rows[i][1].ToString();
                        string kcfs = (null == ds.Tables[0].Rows[i][2]) ? string.Empty : ds.Tables[0].Rows[i][2].ToString();

                        int iKcfs = string.IsNullOrEmpty(kcfs) ? -1 : int.Parse(kcfs); //扣除分数

                        JudgementRule jRule = new JudgementRule(kflx, iKcfs);

                        if (!string.IsNullOrEmpty(cwbh) && !dicJudgementRule.ContainsKey(cwbh))
                        {
                            dicJudgementRule.Add(cwbh, jRule);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.GetLogger().ErrorFormat("catch an error : {0}", e.Message);
                return false;
            }

            if (0 == dicJudgementRule.Count)
            {
                Log.GetLogger().ErrorFormat("初始化扣分规则信息失败，数据库扣分规则表解析结果为空");
                return false;
            }

            Log.GetLogger().InfoFormat("初始化扣分规则信息成功");
            return true;
        }

        private bool InitDevices()
        {
            dicCars.Clear();
            
            //获取 SDK Build
            if (!GetSDKBuildVersion())
            {
                return false;
            }

            //窗口初始化（看不懂是什么作用，这里先翻译C++代码）
            int kch = 0;
            int count = 4;
            string key = string.Empty;
            m_dispalyShow[4] = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_DISPLAY, BaseDefine.CONFIG_SECTION_CONFIG,
                BaseDefine.CONFIG_KEY_VIDEOWND, 1);
            for (int i = 0; i < count; i++)
            {
                key = string.Format("{0}{1}", BaseDefine.CONFIG_KEY_DISPLAY, i + 1);
                m_dispalyShow[i] = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_DISPLAY, BaseDefine.CONFIG_SECTION_CONFIG,
                    key, i);
                kch += m_dispalyShow[i];
            }
            if (kch != 6) 
            {
                for(int i = 0; i < count; i++)
                {
                    key = string.Format("{0}{1}", BaseDefine.CONFIG_KEY_DISPLAY, i + 1);
                    string value = i.ToString();
                    INIOperator.INIWriteValue(BaseDefine.CONFIG_FILE_PATH_DISPLAY, BaseDefine.CONFIG_SECTION_CONFIG, key, value);
                    m_dispalyShow[i] = i;
                }
            }
            Log.GetLogger().InfoFormat("子窗口配置[%d,%d,%d,%d],音频窗口[%d]", m_dispalyShow[0], m_dispalyShow[1],
                m_dispalyShow[2], m_dispalyShow[3], m_dispalyShow[4]);

            //合码器初始化
            int nNum = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_CAR, BaseDefine.CONFIG_SECTION_JMQ,
                BaseDefine.CONFIG_KEY_NUM, 0);    //合码器数量
            int nEven = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_DISPLAY, BaseDefine.CONFIG_SECTION_CONFIG,
                BaseDefine.CONFIG_KEY_EVEN, 0);    //是否隔行合码
            int nKskm = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_ENV, BaseDefine.CONFIG_SECTION_CONFIG,
                BaseDefine.CONFIG_KEY_KSKM, 0);    //考试科目
            int nWnd2 = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_DISPLAY, BaseDefine.CONFIG_SECTION_CONFIG,
                BaseDefine.CONFIG_KEY_WND2, 1);    //画面二状态
            int nHMQ = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_ENV, BaseDefine.CONFIG_SECTION_CONFIG,
                BaseDefine.CONFIG_KEY_HMQ, 0);  //配置的是合码器还是解码器
            if (0 == nNum)
            {
                Log.GetLogger().ErrorFormat("合码器数量为0，请检查配置文件");
                return false;
            }
            Log.GetLogger().InfoFormat("读取到 {0} 台合码器，EVEN = {1}, 科目{2}，画面2={3}", nNum, nEven, nKskm, nWnd2);

            string errorMsg = string.Empty;
            for (int i = 1; i <= nNum; i++)
            {
                string strConf = INIOperator.INIGetStringValue(BaseDefine.CONFIG_FILE_PATH_CAR, BaseDefine.CONFIG_SECTION_JMQ,
                    i.ToString(), string.Empty);
                string[] confArray = BaseMethod.SplitString(strConf, BaseDefine.SPLIT_CHAR_COMMA, out errorMsg);
                if (!string.IsNullOrEmpty(errorMsg) || confArray.Length != 4)
                {
                    Log.GetLogger().ErrorFormat("合码器配置错误，请检查配置文件。section = {0}, key = {1}", BaseDefine.CONFIG_SECTION_JMQ, i.ToString());
                    return false;
                }

                string ipAddress = confArray[0];    //合码器地址
                string user = confArray[1];     //用户名
                string password = confArray[2];     //密码
                string port = confArray[3];     //端口
                if (string.IsNullOrEmpty(ipAddress) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(port))
                {
                    Log.GetLogger().ErrorFormat("合码器配置错误，请检查配置文件。section = {0}, key = {1}", BaseDefine.CONFIG_SECTION_JMQ, i.ToString());
                    return false;
                }
                Log.GetLogger().InfoFormat("准备对合码器 {0} 进行初始化，ip={1}, port={2}, user={3}, password={4}", i, ipAddress, port, user, password);

                //登录设备
                if (!InitHMQ(ipAddress, user, password, port))
                {
                    return false;
                }

                int displayChanCount = (1 == nHMQ) ? m_struDecAbility.struDviInfo.byChanNums : m_struDecAbility.struBncInfo.byChanNums;
                string sectionJMQ = string.Format("JMQ{0}", i);
                for (int j  = 0; j < displayChanCount; j++)  //DVI、BNC 个数循环
                {
                    if ((1 == nEven) && (j % 2 == 1))
                    {
                        continue;
                    }

                    string keyBNC = string.Format("BNC{0}", j + 1); //从 1 开始
                    int nKch = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_CAR, sectionJMQ, keyBNC, -1);
                    if (-1 == nKch)  //没有配置
                    {
                        Log.GetLogger().InfoFormat("合码器 JMQ{0} 的 BNC 通道 {1} 处于空闲，可以配置。", i, keyBNC);
                        continue; 
                    }

                    //检查通道配置及初始化
                    if (1 == nHMQ)
                    {
                        if (!CheckDVIChan(nKch, j)) //合码器
                        {
                            Log.GetLogger().ErrorFormat("通道检测及初始化错误，考车号={0}，DVI={1}", nKch, j);
                        }
                    }
                    else
                    {
                        if (!CheckBNCChan(nKch, j)) //解码器
                        {
                            Log.GetLogger().ErrorFormat("通道检测及初始化错误，考车号={0}，BNC={1}", nKch, j);
                        }
                    }
                }

            }

            return true;
        }

        //登录设备
        private bool InitHMQ(string ip, string user, string pwd, string port)
        {
            try
            {
                //登录设备
                int nPort = string.IsNullOrEmpty(port) ? 8000 : int.Parse(port);
                CHCNetSDK.NET_DVR_DEVICEINFO_V30 m_struDeviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
                m_userId = CHCNetSDK.NET_DVR_Login_V30(ip, nPort, user, pwd, ref m_struDeviceInfo);
                if (-1 == m_userId)
                {
                    m_iErrorCode = CHCNetSDK.NET_DVR_GetLastError();
                    Log.GetLogger().ErrorFormat("NET_DVR_Login_V30 failed, error code = {0}", m_iErrorCode);
                    return false;
                }

                //获取设备能力集
                int nSize = Marshal.SizeOf(m_struDecAbility);
                IntPtr ptrDecAbility = Marshal.AllocHGlobal(nSize);
                Marshal.StructureToPtr(m_struDecAbility, ptrDecAbility, false);
                if (!CHCNetSDK.NET_DVR_GetDeviceAbility(m_userId, CHCNetSDK.MATRIXDECODER_ABILITY_V41, IntPtr.Zero, 0, ptrDecAbility, (uint)nSize))
                {
                    m_iErrorCode = CHCNetSDK.NET_DVR_GetLastError();
                    Log.GetLogger().ErrorFormat("Get MATRIXDECODER_ABILITY_V41 failed, error code = {0}", m_iErrorCode);
                    return false;
                }
                m_struDecAbility = (CHCNetSDK.NET_DVR_MATRIX_ABILITY_V41)Marshal.PtrToStructure(ptrDecAbility, 
                    typeof(CHCNetSDK.NET_DVR_MATRIX_ABILITY_V41));
                Log.GetLogger().InfoFormat("合码器ip={0}, DecN={1}, BncN={2}", ip, m_struDecAbility.byDecChanNums, 
                    m_struDecAbility.struBncInfo.byChanNums);

                //设置设备自动重启
                CHCNetSDK.NET_DVR_AUTO_REBOOT_CFG struRebootCfg = new CHCNetSDK.NET_DVR_AUTO_REBOOT_CFG();
                nSize = Marshal.SizeOf(struRebootCfg);
                IntPtr ptrRebootCfg = Marshal.AllocHGlobal(nSize);
                Marshal.StructureToPtr(struRebootCfg, ptrRebootCfg, false);
                uint lpBytesReturned = 0;
                if (!CHCNetSDK.NET_DVR_GetDVRConfig(m_userId, CHCNetSDK.NET_DVR_GET_AUTO_REBOOT_CFG,
                    0, ptrRebootCfg, (uint)nSize, ref lpBytesReturned))
                {
                    m_iErrorCode = CHCNetSDK.NET_DVR_GetLastError();
                    Log.GetLogger().ErrorFormat("NET_DVR_GetDVRConfig 获取重启参数失败, error code = {0}", m_iErrorCode);
                    return false;
                }
                struRebootCfg = (CHCNetSDK.NET_DVR_AUTO_REBOOT_CFG)Marshal.PtrToStructure(ptrRebootCfg,
                    typeof(CHCNetSDK.NET_DVR_AUTO_REBOOT_CFG));
                if (7 != struRebootCfg.struRebootTime.byDate)
                {
                    struRebootCfg.dwSize = (uint)nSize;
                    struRebootCfg.struRebootTime.byDate = 7;
                    struRebootCfg.struRebootTime.byHour = 5;
                    struRebootCfg.struRebootTime.byMinute = 9;
                    IntPtr ptrRebootCfgNew = Marshal.AllocHGlobal(nSize);
                    Marshal.StructureToPtr(struRebootCfg, ptrRebootCfgNew, false);
                    if (!CHCNetSDK.NET_DVR_SetDVRConfig(m_userId, CHCNetSDK.NET_DVR_SET_AUTO_REBOOT_CFG, 0, 
                        ptrRebootCfgNew, (uint)nSize))
                    {
                        m_iErrorCode = CHCNetSDK.NET_DVR_GetLastError();
                        Log.GetLogger().ErrorFormat("NET_DVR_SetDVRConfig 设置自动重启失败, errorCode = {0}", m_iErrorCode);
                        return false;
                    }
                    Log.GetLogger().InfoFormat("NET_DVR_SetDVRConfig 设置自动重启成功");
                }
            }
            catch (Exception e)
            {
                Log.GetLogger().InfoFormat("InitHMQ catch an error : {0}", e.Message);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 解码器通道检测及初始化
        /// </summary>
        /// <param name="kch">考车号</param>
        /// <param name="bnc">BNC 编号</param>
        /// <returns></returns>
        private bool CheckBNCChan(int kch, int bnc)
        {
            uint lpdwEnable = 0;
            uint dwDecChanNum = 0;
            byte[] byDecChan = new byte[4];

            //解码通道检测
            for (int i = 0; i < 4; i++)
            {
                byDecChan[i] = (byte)(m_struDecAbility.byStartChan + bnc * 4 + i);
                dwDecChanNum = byDecChan[i];
                if (CHCNetSDK.NET_DVR_MatrixGetDecChanEnable(m_userId, dwDecChanNum, ref lpdwEnable))
                {
                    if (0 == lpdwEnable)    //取出的值为 0 表示关闭。
                    {
                        if (!CHCNetSDK.NET_DVR_MatrixSetDecChanEnable(m_userId, dwDecChanNum, 1))    //打开通道
                        {
                            m_iErrorCode = CHCNetSDK.NET_DVR_GetLastError();
                            Log.GetLogger().ErrorFormat("NET_DVR_MatrixSetDecChanEnable failed, error code = {0}，打开通道失败", m_iErrorCode);
                            return false;
                        }
                    }
                }
            }

            //显示通道检测
            CHCNetSDK.NET_DVR_MATRIX_VOUTCFG m_DispChanCfg = new CHCNetSDK.NET_DVR_MATRIX_VOUTCFG();
            uint dwDispChan = (uint)(m_struDecAbility.struBncInfo.byStartChan + bnc);
            if (!CHCNetSDK.NET_DVR_MatrixGetDisplayCfg_V41(m_userId, dwDispChan, ref m_DispChanCfg))
            {
                m_iErrorCode = CHCNetSDK.NET_DVR_GetLastError();
                Log.GetLogger().ErrorFormat("NET_DVR_MatrixGetDisplayCfg_V41 failed, error code = {0}，获取显示通道配置失败", m_iErrorCode);
                return false;
            }

            if (m_DispChanCfg.dwWindowMode != 4 
                || m_DispChanCfg.byJoinDecChan[0] != byDecChan[m_dispalyShow[0]]
                || m_DispChanCfg.byJoinDecChan[1] != byDecChan[m_dispalyShow[1]]
                || m_DispChanCfg.byJoinDecChan[2] != byDecChan[m_dispalyShow[2]]
                || m_DispChanCfg.byJoinDecChan[3] != byDecChan[m_dispalyShow[3]])   //显示通道不是四分割输出
            {
                m_DispChanCfg.byJoinDecChan[0] = byDecChan[m_dispalyShow[0]];
                m_DispChanCfg.byJoinDecChan[1] = byDecChan[m_dispalyShow[1]];
                m_DispChanCfg.byJoinDecChan[2] = byDecChan[m_dispalyShow[2]];
                m_DispChanCfg.byJoinDecChan[3] = byDecChan[m_dispalyShow[3]];
                m_DispChanCfg.byAudio = 1;  //开启音频
                m_DispChanCfg.byAudioWindowIdx = (byte)m_dispalyShow[4];  //音频子窗口 1
                m_DispChanCfg.byVedioFormat = 2;    //视频制式，1-NTSC，2-PAL //该项配置与合码器不同
                m_DispChanCfg.dwResolution = 0; //该项配置与合码器不同
                m_DispChanCfg.dwWindowMode = 4;
                m_DispChanCfg.byScale = 0;  //真实

                //解码器设置显示通道配置
                if(!CHCNetSDK.NET_DVR_MatrixSetDisplayCfg_V41(m_userId, dwDispChan, ref m_DispChanCfg))
                {
                    m_iErrorCode = CHCNetSDK.NET_DVR_GetLastError();
                    if (29 == m_iErrorCode)
                    {
                        Log.GetLogger().ErrorFormat("错误29:设备操作失败,请将合码器完全恢复下.设备管理=>恢复默认参数=>完成恢复");
                    }
                    Log.GetLogger().ErrorFormat("NET_DVR_MatrixSetDisplayCfg_V41 failed, error code = {0}，设置显示通道配置失败", m_iErrorCode);
                    return false;
                }
            }

            //考车初始化
            CarManager car = new CarManager();
            car.InitCar(kch, m_userId, byDecChan);
            if (!dicCars.ContainsKey(kch))
            {
                dicCars.Add(kch, car);
            }

            return true;
        }

        /// <summary>
        /// 合码器通道检测及初始化
        /// </summary>
        /// <param name="kch">考车号</param>
        /// <param name="dvi">dvi 编号</param>
        /// <returns></returns>
        private bool CheckDVIChan(int kch, int dvi)
        {
            uint lpdwEnable = 0;
            uint dwDecChanNum = 0;
            byte[] byDecChan = new byte[4];

            //解码通道检测
            for (int i = 0; i < 4; i++)
            {
                byDecChan[i] = (byte)(m_struDecAbility.byStartChan + dvi * 4 + i);
                dwDecChanNum = byDecChan[i];
                if (CHCNetSDK.NET_DVR_MatrixGetDecChanEnable(m_userId, dwDecChanNum, ref lpdwEnable))
                {
                    if (0 == lpdwEnable)    //取出的值为 0 表示关闭。
                    {
                        if (!CHCNetSDK.NET_DVR_MatrixSetDecChanEnable(m_userId, dwDecChanNum, 1))    //打开通道
                        {
                            m_iErrorCode = CHCNetSDK.NET_DVR_GetLastError();
                            Log.GetLogger().ErrorFormat("NET_DVR_MatrixSetDecChanEnable failed, error code = {0}，打开通道失败", m_iErrorCode);
                            return false;
                        }
                    }
                }
            }

            //显示通道检测
            CHCNetSDK.NET_DVR_MATRIX_VOUTCFG m_DispChanCfg = new CHCNetSDK.NET_DVR_MATRIX_VOUTCFG();
            uint dwDispChan = (uint)(m_struDecAbility.struDviInfo.byStartChan + dvi);
            if (!CHCNetSDK.NET_DVR_MatrixGetDisplayCfg_V41(m_userId, dwDispChan, ref m_DispChanCfg))
            {
                m_iErrorCode = CHCNetSDK.NET_DVR_GetLastError();
                Log.GetLogger().ErrorFormat("NET_DVR_MatrixGetDisplayCfg_V41 failed, error code = {0}，获取显示通道配置失败", m_iErrorCode);
                return false;
            }

            if (m_DispChanCfg.dwWindowMode != 4
                || m_DispChanCfg.byJoinDecChan[0] != byDecChan[m_dispalyShow[0]]
                || m_DispChanCfg.byJoinDecChan[1] != byDecChan[m_dispalyShow[1]]
                || m_DispChanCfg.byJoinDecChan[2] != byDecChan[m_dispalyShow[2]]
                || m_DispChanCfg.byJoinDecChan[3] != byDecChan[m_dispalyShow[3]])   //显示通道不是四分割输出
            {
                m_DispChanCfg.byJoinDecChan[0] = byDecChan[m_dispalyShow[0]];
                m_DispChanCfg.byJoinDecChan[1] = byDecChan[m_dispalyShow[1]];
                m_DispChanCfg.byJoinDecChan[2] = byDecChan[m_dispalyShow[2]];
                m_DispChanCfg.byJoinDecChan[3] = byDecChan[m_dispalyShow[3]];
                m_DispChanCfg.byAudio = 1;  //开启音频
                m_DispChanCfg.byAudioWindowIdx = (byte)m_dispalyShow[4];  //音频子窗口 1
                m_DispChanCfg.byVedioFormat = 1;    //视频制式，1-NTSC，2-PAL //该项配置与合码器不同
                m_DispChanCfg.dwResolution = 67207228;  //该项配置与合码器不同
                m_DispChanCfg.dwWindowMode = 4;
                m_DispChanCfg.byScale = 0;  //真实

                //解码器设置显示通道配置
                if (!CHCNetSDK.NET_DVR_MatrixSetDisplayCfg_V41(m_userId, dwDispChan, ref m_DispChanCfg))
                {
                    m_iErrorCode = CHCNetSDK.NET_DVR_GetLastError();
                    if (29 == m_iErrorCode)
                    {
                        Log.GetLogger().ErrorFormat("错误29:设备操作失败,请将合码器完全恢复下.设备管理=>恢复默认参数=>完成恢复");
                    }
                    Log.GetLogger().ErrorFormat("NET_DVR_MatrixSetDisplayCfg_V41 failed, error code = {0}，设置显示通道配置失败", m_iErrorCode);
                    return false;
                }
            }

            //考车初始化
            CarManager car = new CarManager();
            car.InitCar(kch, m_userId, byDecChan);
            if (!dicCars.ContainsKey(kch))
            {
                dicCars.Add(kch, car);
            }

            return true;
        }

        //开始运行
        private bool RunMap()
        {
            string key = string.Empty;
            CameraConf cameraConf = new CameraConf();

            //地图
            Image imgMap = null;
            int nLoadMap = BaseMethod.INIGetIntValue(BaseDefine.CONFIG_FILE_PATH_ENV, BaseDefine.CONFIG_SECTION_CONFIG,
                BaseDefine.CONFIG_KEY_LOADMAP, 0);
            if (1 == nLoadMap)
            {
                try
                {
                    imgMap = Image.FromFile(BaseDefine.IMG_PATH_MAPN);
                }
                catch(Exception e)
                {
                    Log.GetLogger().ErrorFormat("Image.FromFile catch an error : {0}", e.Message);
                }
            }

            foreach (int iKch in dicCars.Keys)
            {
                Thread.Sleep(10);

                CarManager carManager = dicCars[iKch];

                //动态解码
                key = string.Format("考车{0}_1", iKch);
                if (!dicCameras.ContainsKey(key))
                {
                    Log.GetLogger().ErrorFormat("{0} 摄像头未配置，请检查", key);
                }
                else
                {
                    cameraConf = dicCameras[key];
                    carManager.StartDynamicDecode(cameraConf, 0);
                }

                key = string.Format("考车{0}_2", iKch);
                if (!dicCameras.ContainsKey(key))
                {
                    Log.GetLogger().ErrorFormat("{0} 摄像头未配置，请检查", key);
                }
                else
                {
                    cameraConf = dicCameras[key];
                    carManager.StartDynamicDecode(cameraConf, 1);
                }

                try
                {
                    //被动解码
                    int thirdPH = -1;
                    int fourthPH = -1;
                    if (carManager.StartPassiveDecode(2, ref thirdPH) && carManager.StartPassiveDecode(3, ref fourthPH))
                    {
                        ExamProcedure examProcedure = new ExamProcedure();
                        if (examProcedure.Init(m_userId, iKch, thirdPH, fourthPH, imgMap))
                        {
                            dicExamProcedures.Add(iKch, examProcedure);
                        }
                    }
                    else
                    {
                        Log.GetLogger().ErrorFormat("被动解码失败，kch={0}", iKch);
                    }
                }
                catch (Exception e)
                {
                    Log.GetLogger().ErrorFormat("catch an error : {0}", e.Message);
                }

            }

            return true;
        }
    }
}
