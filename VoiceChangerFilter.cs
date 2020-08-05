using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace wutai
{
    public class VoiceChangerFilter : MonoBehaviour
    {
        int channels;
        [Range(0, 5)]
        public float _formant;
        [Range(0, 3)]
        public float _pitch;
        public bool _denoise;
        [DllImport("world_dll")]
        private static extern int filter(double[] datas, int data_size, int fs, float pitch, float formant);

        [DllImport("world_dll")]
        private static extern int filter_debug(double[] datas, int data_size, int fs, float pitch, float formant, int[] time);

        [DllImport("DenoiseDll")]
        private static extern void denoiseData2Data(float[] indata, float[] outdata, int sampleRate, long sampleCount, int channels);

        int outputSampleRate = 48000;
        SetupMicphone setupMicphone;

        public Dictionary<int, SettingData> Record;

        void Awake()
        {
            outputSampleRate = AudioSettings.outputSampleRate;
        }

        void Start()
        {
            AudioSource audioSource = GetComponent<AudioSource>();
            setupMicphone = GetComponent<SetupMicphone>();
            //通过外部配置文件设置变声参数
            DataTool.Instance.Load_Data();
            Record = DataTool.Instance.Record;

            _formant = Record[setupMicphone.id].format;
            _pitch = Record[setupMicphone.id].pitch;
            _denoise = Record[setupMicphone.id].denoise;

            if (audioSource != null && audioSource.clip != null)
            {
                outputSampleRate = audioSource.clip.frequency;
            }

            _buffer = new double[_bufferNum][];
            for (int i = 0; i < _bufferNum; i++)
            {
                _buffer[i] = new double[(int)(outputSampleRate * _bufferLimitTime)];
            }
            _bufferUseSize = new int[_bufferNum];

            _started = true;
        }

        double[][] _buffer;
        int[] _bufferUseSize;
        int _bufferingSeek = 0;
        int _playingSeek = 0;
        int _bufferingIndex = 0;
        int _playingIndex = 0;
        int _bufferNum = 4;
        float _bufferTime = 0.3f;
        float _bufferLimitTime = 0.4f;  //buffer时长
        bool _started = false;

        void copyBuffer(double[] src_buffer, double[] dst_buffer, int src_startIndex, int dst_startIndex, int size)
        {
            //溢出
            if (src_buffer.Length < src_startIndex + size || dst_buffer.Length < dst_startIndex + size)
            {
                Debug.Log("error");
            }

            for (int i = 0; i < size; i++)
            {
                dst_buffer[dst_startIndex + i] = src_buffer[src_startIndex + i];
            }
        }

        void memBuffer(double[] dst_buffer, int dst_startIndex, double value, int size)
        {
            for (int i = 0; i < size; i++)
            {
                dst_buffer[dst_startIndex + i] = value;
            }
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            this.channels = channels;
            if (data.Length == 0)
                return;

            if (!_started)
                return;

            if (channels == 1)
            {
                double[] monoral_data = new double[data.Length];

                for (int i = 0; i < data.Length; i++)
                {
                    monoral_data[i] = data[i];
                }

                OnProcess(monoral_data);

                for (int i = 0; i < data.Length; i++)
                {
                    monoral_data[i] = (float)data[i];
                }
            }
            else
            {
                int monoralDataLength = data.Length / channels;
                double[] monoral_data = new double[monoralDataLength];

                for (int i = 0; i < monoralDataLength; i++)
                {
                    monoral_data[i] = data[i * channels];
                }
                OnProcess(monoral_data);
                //恢复data
                for (int i = 0; i < data.Length; i += channels)
                {
                    int j = (int)(i / channels);
                    for (int k = 0; k < channels; k++)
                    {
                        data[i + k] = (float)monoral_data[j];
                    }
                }
            }
        }


        void StartThread(double[] data, int data_size, int fs, float pitch, float formant)
        {
            FilterParam param = new FilterParam();
            param.data = data;
            param.data_size = data_size;
            param.fs = fs;
            param.pitch = pitch;
            param.formant = formant;
            Thread thread = new Thread(new ParameterizedThreadStart(ThreadWork));
            thread.Start(param);
        }

        void ThreadWork(object _param)
        {
            FilterParam param = (FilterParam)_param;
            //变声
            filter(param.data, param.data_size, param.fs, param.pitch, param.formant);
            //降噪
            if (_denoise)
            {
                float[] indata = new float[param.data.Length];
                float[] outdata = new float[param.data.Length];
                for (int i = 0; i < indata.Length; i++)
                {
                    indata[i] = (float)param.data[i];
                }
                denoiseData2Data(indata, outdata, outputSampleRate, param.data_size, channels);
                for (int i = 0; i < indata.Length; i++)
                {
                    param.data[i] = outdata[i];
                }
            }
        }

        void OnProcess(double[] monoral_data)
        {
            //data数据转到buffer中
            if (_bufferingSeek < _bufferTime * outputSampleRate)
            {
                copyBuffer(monoral_data, _buffer[_bufferingIndex], 0, _bufferingSeek, monoral_data.Length);
                _bufferingSeek += monoral_data.Length;
                _bufferUseSize[_bufferingIndex] = _bufferingSeek;
            }
            else //跳到下个buffer
            {
                StartThread(_buffer[_bufferingIndex], _bufferUseSize[_bufferingIndex], outputSampleRate, Mathf.Max(_pitch, 0.3f), _formant);

                _bufferingIndex = (_bufferingIndex + 1) % _bufferNum;
                _bufferingSeek = 0;
                //先将前面那组的数据转化，之后和上边一样操作
                copyBuffer(monoral_data, _buffer[_bufferingIndex], 0, _bufferingSeek, monoral_data.Length);
                _bufferingSeek += monoral_data.Length;
                _bufferUseSize[_bufferingIndex] = _bufferingSeek;
            }

            //变声处理
            //这里就是将变声后的数据放入monoral_data，buffer大小为4，但是差了2个时间间隔
            //当前index为2时，将audio的实时date转化好后放入index为0的里边，差了2个bufferTime
            if ((_playingIndex + 2) % _bufferNum == _bufferingIndex)
            {
                //播放的data
                if (_playingSeek + monoral_data.Length < _bufferUseSize[_playingIndex])
                {
                    copyBuffer(_buffer[_playingIndex], monoral_data, _playingSeek, 0, monoral_data.Length);
                    _playingSeek += monoral_data.Length;
                }
                else
                {
                    int copyBufferSize = _bufferUseSize[_playingIndex] - _playingSeek;
                    copyBuffer(_buffer[_playingIndex], monoral_data, _playingSeek, 0, copyBufferSize);   //剩下的缓冲器
                    _playingIndex = (_playingIndex + 1) % _bufferNum;   //进入下一个缓冲器
                    _playingSeek = 0;

                    int nextCopyBufferSize = monoral_data.Length - copyBufferSize;
                    copyBuffer(_buffer[_playingIndex], monoral_data, _playingSeek, copyBufferSize, nextCopyBufferSize);
                    _playingSeek += nextCopyBufferSize;
                }
            }
            else
            {
                memBuffer(monoral_data, 0, 0f, monoral_data.Length);
            }
        }
    }

    //filter所用参数
    public class FilterParam
    {
        public double[] data;
        public int data_size;
        public int fs;
        public float pitch;
        public float formant;
    }
}