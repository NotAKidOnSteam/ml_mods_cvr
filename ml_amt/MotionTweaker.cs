﻿using ABI_RC.Core.Player;
using ABI_RC.Systems.IK.SubSystems;
using ABI_RC.Systems.MovementSystem;
using RootMotion.FinalIK;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ml_amt
{
    [DisallowMultipleComponent]
    class MotionTweaker : MonoBehaviour
    {
        static readonly FieldInfo ms_grounded = typeof(MovementSystem).GetField("_isGrounded", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo ms_groundedRaw = typeof(MovementSystem).GetField("_isGroundedRaw", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo ms_rootVelocity = typeof(IKSolverVR).GetField("rootVelocity", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly int ms_emoteHash = Animator.StringToHash("Emote");

        enum ParameterType
        {
            Upright,
            GroundedRaw,
            Moving
        }

        enum ParameterSyncType
        {
            Local,
            Synced
        }

        struct AdditionalParameterInfo
        {
            public ParameterType m_type;
            public ParameterSyncType m_sync;
            public string m_name;
            public int m_hash; // For local only
        }

        enum PoseState
        {
            Standing = 0,
            Crouching,
            Proning
        }

        static readonly Vector4 ms_pointVector = new Vector4(0f, 0f, 0f, 1f);

        VRIK m_vrIk = null;
        int m_locomotionLayer = 0;
        float m_ikWeight = 1f; // Original weight
        float m_locomotionWeight = 1f; // Original weight
        float m_avatarScale = 1f; // Instantiated scale
        Transform m_avatarHips = null;
        float m_viewPointHeight = 1f;
        bool m_isInVR = false;
        public static bool ms_fptActive = false;

        bool m_avatarReady = false;
        bool m_compatibleAvatar = false;
        float m_upright = 1f;
        PoseState m_poseState = PoseState.Standing;
        bool m_grounded = false;
        bool m_groundedRaw = false;
        bool m_moving = false;

        bool m_ikOverrideCrouch = true;
        float m_crouchLimit = 0.65f;
        bool m_customCrouchLimit = false;

        bool m_ikOverrideProne = true;
        float m_proneLimit = 0.3f;
        bool m_customProneLimit = false;

        bool m_poseTransitions = true;
        bool m_adjustedMovement = true;
        bool m_ikOverrideFly = true;
        bool m_ikOverrideJump = true;

        bool m_customLocomotionOffset = false;
        Vector3 m_locomotionOffset = Vector3.zero;

        bool m_detectEmotes = true;
        bool m_emoteActive = false;

        bool m_followHips = true;
        Vector3 m_hipsToPlayer = Vector3.zero;

        readonly List<AdditionalParameterInfo> m_parameters = null;

        public MotionTweaker()
        {
            m_parameters = new List<AdditionalParameterInfo>();
        }

        void Start()
        {
            m_isInVR = Utils.IsInVR();

            Settings.IKOverrideCrouchChange += this.SetIKOverrideCrouch;
            Settings.CrouchLimitChange += this.SetCrouchLimit;
            Settings.IKOverrideProneChange += this.SetIKOverrideProne;
            Settings.ProneLimitChange += this.SetProneLimit;
            Settings.PoseTransitionsChange += this.SetPoseTransitions;
            Settings.AdjustedMovementChange += this.SetAdjustedMovement;
            Settings.IKOverrideFlyChange += this.SetIKOverrideFly;
            Settings.IKOverrideJumpChange += this.SetIKOverrideJump;
            Settings.DetectEmotesChange += this.SetDetectEmotes;
            Settings.FollowHipsChange += this.SetFollowHips;
        }

        void OnDestroy()
        {
            Settings.IKOverrideCrouchChange -= this.SetIKOverrideCrouch;
            Settings.CrouchLimitChange -= this.SetCrouchLimit;
            Settings.IKOverrideProneChange -= this.SetIKOverrideProne;
            Settings.ProneLimitChange -= this.SetProneLimit;
            Settings.PoseTransitionsChange -= this.SetPoseTransitions;
            Settings.AdjustedMovementChange -= this.SetAdjustedMovement;
            Settings.IKOverrideFlyChange -= this.SetIKOverrideFly;
            Settings.IKOverrideJumpChange -= this.SetIKOverrideJump;
            Settings.DetectEmotesChange -= this.SetDetectEmotes;
            Settings.FollowHipsChange -= this.SetFollowHips;
        }

        void Update()
        {
            if(m_avatarReady)
            {
                m_grounded = (bool)ms_grounded.GetValue(MovementSystem.Instance);
                m_groundedRaw = (bool)ms_groundedRaw.GetValue(MovementSystem.Instance);
                m_moving = !Mathf.Approximately(MovementSystem.Instance.movementVector.magnitude, 0f);

                // Update upright
                Matrix4x4 l_hmdMatrix = PlayerSetup.Instance.transform.GetMatrix().inverse * (m_isInVR ? PlayerSetup.Instance.vrHeadTracker.transform.GetMatrix() : PlayerSetup.Instance.desktopCameraRig.transform.GetMatrix());
                float l_currentHeight = Mathf.Clamp((l_hmdMatrix * ms_pointVector).y, 0f, float.MaxValue);
                float l_avatarScale = (m_avatarScale > 0f) ? (PlayerSetup.Instance._avatar.transform.localScale.y / m_avatarScale) : 0f;
                float l_avatarViewHeight = Mathf.Clamp(m_viewPointHeight * l_avatarScale, 0f, float.MaxValue);
                m_upright = Mathf.Clamp(((l_avatarViewHeight > 0f) ? (l_currentHeight / l_avatarViewHeight) : 0f), 0f, 1f);
                PoseState l_poseState = (m_upright <= m_proneLimit) ? PoseState.Proning : ((m_upright <= m_crouchLimit) ? PoseState.Crouching : PoseState.Standing);

                if(m_avatarHips != null)
                {
                    Vector4 l_hipsToPoint = (PlayerSetup.Instance.transform.GetMatrix().inverse * m_avatarHips.GetMatrix()) * ms_pointVector;
                    m_hipsToPlayer.Set(l_hipsToPoint.x, 0f, l_hipsToPoint.z);
                }

                if(m_isInVR && (m_vrIk != null) && m_vrIk.enabled)
                {
                    if(m_poseState != l_poseState)
                    {
                        // Weird fix of torso shaking
                        if(m_ikOverrideCrouch && (l_poseState == PoseState.Standing))
                            ms_rootVelocity.SetValue(m_vrIk.solver, Vector3.zero);
                        if(m_ikOverrideProne && !m_ikOverrideCrouch && (l_poseState == PoseState.Crouching))
                            ms_rootVelocity.SetValue(m_vrIk.solver, Vector3.zero);
                    }

                    if(m_adjustedMovement)
                    {
                        MovementSystem.Instance.ChangeCrouch(l_poseState == PoseState.Crouching);
                        MovementSystem.Instance.ChangeProne(l_poseState == PoseState.Proning);

                        if(!m_poseTransitions)
                        {
                            PlayerSetup.Instance.animatorManager.SetAnimatorParameterBool("Crouching", false);
                            PlayerSetup.Instance.animatorManager.SetAnimatorParameterBool("Prone", false);
                        }
                    }

                    if(m_poseTransitions)
                    {
                        PlayerSetup.Instance.animatorManager.SetAnimatorParameterBool("Crouching", (l_poseState == PoseState.Crouching) && !m_compatibleAvatar && !Utils.IsInFullbody());
                        PlayerSetup.Instance.animatorManager.SetAnimatorParameterBool("Prone", (l_poseState == PoseState.Proning) && !m_compatibleAvatar && !Utils.IsInFullbody());
                    }
                }

                m_poseState = l_poseState;

                m_emoteActive = false;
                if(m_detectEmotes && (m_locomotionLayer >= 0))
                {
                    AnimatorStateInfo l_animState = PlayerSetup.Instance._animator.GetCurrentAnimatorStateInfo(m_locomotionLayer);
                    m_emoteActive = (l_animState.tagHash == ms_emoteHash);
                }

                if(m_parameters.Count > 0)
                {
                    foreach(AdditionalParameterInfo l_param in m_parameters)
                    {
                        switch(l_param.m_type)
                        {
                            case ParameterType.Upright:
                            {
                                switch(l_param.m_sync)
                                {
                                    case ParameterSyncType.Local:
                                        PlayerSetup.Instance._animator.SetFloat(l_param.m_hash, m_upright);
                                        break;
                                    case ParameterSyncType.Synced:
                                        PlayerSetup.Instance.animatorManager.SetAnimatorParameterFloat(l_param.m_name, m_upright);
                                        break;
                                }
                            }
                            break;

                            case ParameterType.GroundedRaw:
                            {
                                switch(l_param.m_sync)
                                {
                                    case ParameterSyncType.Local:
                                        PlayerSetup.Instance._animator.SetBool(l_param.m_hash, m_groundedRaw);
                                        break;
                                    case ParameterSyncType.Synced:
                                        PlayerSetup.Instance.animatorManager.SetAnimatorParameterBool(l_param.m_name, m_groundedRaw);
                                        break;
                                }
                            }
                            break;

                            case ParameterType.Moving:
                            {
                                switch(l_param.m_sync)
                                {
                                    case ParameterSyncType.Local:
                                        PlayerSetup.Instance._animator.SetBool(l_param.m_hash, m_moving);
                                        break;
                                    case ParameterSyncType.Synced:
                                        PlayerSetup.Instance.animatorManager.SetAnimatorParameterBool(l_param.m_name, m_moving);
                                        break;
                                }
                            }
                            break;
                        }
                    }
                }
            }
        }

        public void OnAvatarClear()
        {
            m_vrIk = null;
            m_locomotionLayer = -1;
            m_grounded = false;
            m_groundedRaw = false;
            m_avatarReady = false;
            m_compatibleAvatar = false;
            m_poseState = PoseState.Standing;
            m_customCrouchLimit = false;
            m_customProneLimit = false;
            m_customLocomotionOffset = false;
            m_locomotionOffset = Vector3.zero;
            m_avatarScale = 1f;
            m_emoteActive = false;
            m_moving = false;
            m_hipsToPlayer = Vector3.zero;
            m_avatarHips = null;
            m_viewPointHeight = 1f;
            m_parameters.Clear();
            ms_fptActive = false;
        }

        public void OnSetupAvatar()
        {
            m_vrIk = PlayerSetup.Instance._avatar.GetComponent<VRIK>();
            m_locomotionLayer = PlayerSetup.Instance._animator.GetLayerIndex("Locomotion/Emotes");
            m_avatarHips = PlayerSetup.Instance._animator.GetBoneTransform(HumanBodyBones.Hips);
            m_viewPointHeight = PlayerSetup.Instance._avatar.GetComponent<ABI.CCK.Components.CVRAvatar>().viewPosition.y;

            // Parse animator parameters
            AnimatorControllerParameter[] l_params = PlayerSetup.Instance._animator.parameters;
            ParameterType[] l_enumParams = (ParameterType[])System.Enum.GetValues(typeof(ParameterType));

            foreach(var l_param in l_params)
            {
                foreach(var l_enumParam in l_enumParams)
                {
                    if(l_param.name.Contains(l_enumParam.ToString()) && (m_parameters.FindIndex(p => p.m_type == l_enumParam) == -1))
                    {
                        bool l_local = (l_param.name[0] == '#');

                        m_parameters.Add(new AdditionalParameterInfo
                        {
                            m_type = l_enumParam,
                            m_sync = (l_local ? ParameterSyncType.Local : ParameterSyncType.Synced),
                            m_name = l_param.name,
                            m_hash = (l_local ? l_param.nameHash : 0)
                        });

                        break;
                    }
                }
            }

            m_compatibleAvatar = m_parameters.Exists(p => p.m_name.Contains("Upright"));
            m_avatarScale = Mathf.Abs(PlayerSetup.Instance._avatar.transform.localScale.y);

            Transform l_customTransform = PlayerSetup.Instance._avatar.transform.Find("CrouchLimit");
            m_customCrouchLimit = (l_customTransform != null);
            m_crouchLimit = m_customCrouchLimit ? Mathf.Clamp(l_customTransform.localPosition.y, 0f, 1f) : Settings.CrouchLimit;

            l_customTransform = PlayerSetup.Instance._avatar.transform.Find("ProneLimit");
            m_customProneLimit = (l_customTransform != null);
            m_proneLimit = m_customProneLimit ? Mathf.Clamp(l_customTransform.localPosition.y, 0f, 1f) : Settings.ProneLimit;

            l_customTransform = PlayerSetup.Instance._avatar.transform.Find("LocomotionOffset");
            m_customLocomotionOffset = (l_customTransform != null);
            m_locomotionOffset = m_customLocomotionOffset ? l_customTransform.localPosition : Vector3.zero;

            // Apply VRIK tweaks
            if(m_vrIk != null)
            {
                if(m_customLocomotionOffset)
                    m_vrIk.solver.locomotion.offset = m_locomotionOffset;

                // Place our pre-solver listener first
                m_vrIk.onPreSolverUpdate.AddListener(this.OnIKPreUpdate);
                m_vrIk.onPostSolverUpdate.AddListener(this.OnIKPostUpdate);
            }

            m_avatarReady = true;
        }

        public void OnCalibrate()
        {
            if(m_avatarReady && BodySystem.enableHipTracking && !BodySystem.enableRightFootTracking && !BodySystem.enableLeftFootTracking && !BodySystem.enableLeftKneeTracking && !BodySystem.enableRightKneeTracking)
            {
                BodySystem.isCalibratedAsFullBody = false;
                BodySystem.TrackingLeftLegEnabled = false;
                BodySystem.TrackingRightLegEnabled = false;
                BodySystem.TrackingLocomotionEnabled = true;

                if(m_vrIk != null)
                    m_vrIk.solver.spine.maxRootAngle = 25f; // I need to rotate my legs, ffs!

                ms_fptActive = true;
            }
        }

        void OnIKPreUpdate()
        {
            bool l_legsOverride = false;

            m_ikWeight = m_vrIk.solver.IKPositionWeight;
            m_locomotionWeight = m_vrIk.solver.locomotion.weight;

            if(m_detectEmotes && m_emoteActive)
                m_vrIk.solver.IKPositionWeight = 0f;

            if((m_ikOverrideCrouch && (m_poseState != PoseState.Standing)) || (m_ikOverrideProne && (m_poseState == PoseState.Proning)))
            {
                m_vrIk.solver.locomotion.weight = 0f;
                l_legsOverride = true;
            }
            if(m_ikOverrideFly && MovementSystem.Instance.flying)
            {
                m_vrIk.solver.locomotion.weight = 0f;
                l_legsOverride = true;
            }

            if(m_ikOverrideJump && !m_grounded && !MovementSystem.Instance.flying)
            {
                m_vrIk.solver.locomotion.weight = 0f;
                l_legsOverride = true;
            }

            bool l_solverActive = !Mathf.Approximately(m_vrIk.solver.IKPositionWeight, 0f);

            if(l_legsOverride && l_solverActive && m_followHips && (!m_moving || (m_poseState == PoseState.Proning)) && m_isInVR && !BodySystem.isCalibratedAsFullBody)
            {
                ABI_RC.Systems.IK.IKSystem.VrikRootController.enabled = false;
                PlayerSetup.Instance._avatar.transform.localPosition = m_hipsToPlayer;
            }
        }

        void OnIKPostUpdate()
        {
            m_vrIk.solver.IKPositionWeight = m_ikWeight;
            m_vrIk.solver.locomotion.weight = m_locomotionWeight;
        }

        public void SetIKOverrideCrouch(bool p_state)
        {
            m_ikOverrideCrouch = p_state;
        }
        public void SetCrouchLimit(float p_value)
        {
            if(!m_customCrouchLimit)
                m_crouchLimit = Mathf.Clamp(p_value, 0f, 1f);
        }
        public void SetIKOverrideProne(bool p_state)
        {
            m_ikOverrideProne = p_state;
        }
        public void SetProneLimit(float p_value)
        {
            if(!m_customProneLimit)
                m_proneLimit = Mathf.Clamp(p_value, 0f, 1f);
        }
        public void SetPoseTransitions(bool p_state)
        {
            m_poseTransitions = p_state;

            if(!m_poseTransitions && m_avatarReady && m_isInVR)
            {
                PlayerSetup.Instance.animatorManager.SetAnimatorParameterBool("Crouching", false);
                PlayerSetup.Instance.animatorManager.SetAnimatorParameterBool("Prone", false);
            }
        }
        public void SetAdjustedMovement(bool p_state)
        {
            m_adjustedMovement = p_state;

            if(!m_adjustedMovement && m_avatarReady && m_isInVR)
            {
                MovementSystem.Instance.ChangeCrouch(false);
                MovementSystem.Instance.ChangeProne(false);
            }
        }
        public void SetIKOverrideFly(bool p_state)
        {
            m_ikOverrideFly = p_state;
        }
        public void SetIKOverrideJump(bool p_state)
        {
            m_ikOverrideJump = p_state;
        }
        public void SetDetectEmotes(bool p_state)
        {
            m_detectEmotes = p_state;
        }
        public void SetFollowHips(bool p_state)
        {
            m_followHips = p_state;
        }
    }
}
