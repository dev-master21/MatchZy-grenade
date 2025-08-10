using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Generic;

namespace MatchZy
{
    public partial class MatchZy
    {
        // Словари для отслеживания состояния симуляции
        private Dictionary<int, bool> jumpSimulationActive = new Dictionary<int, bool>();
        private Dictionary<int, Vector> originalVelocity = new Dictionary<int, Vector>();
        private Dictionary<int, Vector> storedPosition = new Dictionary<int, Vector>();
        private Dictionary<int, PlayerButtons> previousButtons = new Dictionary<int, PlayerButtons>();
        private Dictionary<int, bool> needGroundTeleport = new Dictionary<int, bool>();
        private Dictionary<int, bool> isHoldingW = new Dictionary<int, bool>();
        
        // Точная скорость прыжка в CS2
        private const float JUMP_VELOCITY = 301.993378f;
        private const float FORWARD_VELOCITY = 0f;

        public void InitializeJumpthrowPreview()
        {
            RegisterListener<Listeners.OnTick>(OnJumpthrowPreviewTick);
            
            Server.ExecuteCommand("sv_grenade_trajectory_prac_pipreview 1");
            Server.ExecuteCommand("sv_grenade_trajectory_prac_trailtime 4");
        }

        private void OnJumpthrowPreviewTick()
        {
            if (!isPractice) return;

            var players = Utilities.GetPlayers();
            if (players == null) return;

            foreach (var player in players)
            {
                if (!IsPlayerValid(player) || player.PlayerPawn?.Value == null) 
                    continue;

                var pawn = player.PlayerPawn.Value;
                int userId = player.UserId!.Value;
                
                // Проверяем кнопку R и W
                bool isPressingR = (player.Buttons & PlayerButtons.Reload) != 0;
                bool isPressingW = (player.Buttons & PlayerButtons.Forward) != 0;
                
                // Проверяем, держит ли игрок гранату
                var activeWeapon = pawn.WeaponServices?.ActiveWeapon?.Value;
                bool isHoldingGrenade = activeWeapon != null && IsGrenadeWeapon(activeWeapon.DesignerName);

                bool wasSimulating = jumpSimulationActive.ContainsKey(userId) && jumpSimulationActive[userId];

                if (isHoldingGrenade && isPressingR)
                {
                    if (!wasSimulating)
                    {
                        // Начинаем симуляцию прыжка
                        StartJumpSimulation(player, isPressingW);
                    }
                    else
                    {
                        // Обновляем состояние W
                        if (!isHoldingW.ContainsKey(userId) || isHoldingW[userId] != isPressingW)
                        {
                            isHoldingW[userId] = isPressingW;
                        }
                        
                        // Продолжаем симуляцию
                        MaintainJumpSimulation(player);
                    }
                }
                else if (wasSimulating)
                {
                    // Останавливаем симуляцию
                    StopJumpSimulation(player);
                }
                
                // Сохраняем предыдущие кнопки для следующего тика
                previousButtons[userId] = player.Buttons;
            }
        }

        private void StartJumpSimulation(CCSPlayerController player, bool withForward)
        {
            if (player?.PlayerPawn?.Value == null) return;

            var pawn = player.PlayerPawn.Value;
            int userId = player.UserId!.Value;
            
            // Сохраняем текущую скорость и позицию
            originalVelocity[userId] = new Vector(pawn.AbsVelocity.X, pawn.AbsVelocity.Y, pawn.AbsVelocity.Z);
            var currentPos = pawn.CBodyComponent!.SceneNode!.AbsOrigin;
            storedPosition[userId] = new Vector(currentPos.X, currentPos.Y, currentPos.Z);
            jumpSimulationActive[userId] = true;
            needGroundTeleport[userId] = false;
            isHoldingW[userId] = withForward;
            
            // Устанавливаем вертикальную скорость прыжка
            pawn.AbsVelocity.Z = JUMP_VELOCITY;
            
            // Если зажат W, добавляем горизонтальную скорость
            if (withForward)
            {
                float yaw = pawn.EyeAngles.Y * (float)(Math.PI / 180.0);
                pawn.AbsVelocity.X = (float)Math.Cos(yaw) * FORWARD_VELOCITY;
                pawn.AbsVelocity.Y = (float)Math.Sin(yaw) * FORWARD_VELOCITY;
            }
            else
            {
                pawn.AbsVelocity.X = 0;
                pawn.AbsVelocity.Y = 0;
            }
            
            // Убираем флаг "на земле" для корректной симуляции траектории
            pawn.Flags &= ~((uint)PlayerFlags.FL_ONGROUND);
            
            // Показываем сообщение
            PrintToPlayerCenter(player, $"Jumpthrow Preview{(withForward ? " +W" : "")} (Hold R)");
        }

        private void MaintainJumpSimulation(CCSPlayerController player)
        {
            if (player?.PlayerPawn?.Value == null) return;

            var pawn = player.PlayerPawn.Value;
            int userId = player.UserId!.Value;
            
            // Проверяем, начинает ли игрок бросать гранату
            bool isAttacking = (player.Buttons & PlayerButtons.Attack) != 0 || 
                              (player.Buttons & PlayerButtons.Attack2) != 0;
            var previousButtonState = previousButtons.ContainsKey(userId) ? previousButtons[userId] : (PlayerButtons)0;
            bool wasAttacking = (previousButtonState & PlayerButtons.Attack) != 0 || 
                               (previousButtonState & PlayerButtons.Attack2) != 0;
            
            // Если игрок начал атаку (нажал кнопку броска)
            if (!wasAttacking && isAttacking)
            {
                // Мгновенно телепортируем на землю перед броском с нужной скоростью
                if (storedPosition.TryGetValue(userId, out var groundPos))
                {
                    Vector jumpVelocity = new Vector(0, 0, JUMP_VELOCITY);
                    
                    // Добавляем горизонтальную скорость если был W
                    if (isHoldingW.ContainsKey(userId) && isHoldingW[userId])
                    {
                        float yaw = pawn.EyeAngles.Y * (float)(Math.PI / 180.0);
                        jumpVelocity.X = (float)Math.Cos(yaw) * FORWARD_VELOCITY;
                        jumpVelocity.Y = (float)Math.Sin(yaw) * FORWARD_VELOCITY;
                    }
                    
                    // Поднимаем немного вверх для имитации прыжка
                    Vector jumpPos = new Vector(groundPos.X, groundPos.Y, groundPos.Z + 10.0f);
                    pawn.Teleport(jumpPos, pawn.EyeAngles, jumpVelocity);
                    needGroundTeleport[userId] = true;
                }
            }
            
            // Удерживаем игрока на месте (если не нужен телепорт на землю)
            if (!needGroundTeleport.ContainsKey(userId) || !needGroundTeleport[userId])
            {
                if (storedPosition.TryGetValue(userId, out var storedPos))
                {
                    // Напрямую устанавливаем позицию каждый тик
                    var currentOrigin = pawn.CBodyComponent!.SceneNode!.AbsOrigin;
                    currentOrigin.X = storedPos.X;
                    currentOrigin.Y = storedPos.Y;
                    currentOrigin.Z = storedPos.Z;
                    
                    // Устанавливаем скорость для предпросмотра
                    if (isHoldingW.ContainsKey(userId) && isHoldingW[userId])
                    {
                        float yaw = pawn.EyeAngles.Y * (float)(Math.PI / 180.0);
                        pawn.AbsVelocity.X = (float)Math.Cos(yaw) * FORWARD_VELOCITY;
                        pawn.AbsVelocity.Y = (float)Math.Sin(yaw) * FORWARD_VELOCITY;
                    }
                    else
                    {
                        pawn.AbsVelocity.X = 0;
                        pawn.AbsVelocity.Y = 0;
                    }
                    pawn.AbsVelocity.Z = JUMP_VELOCITY;
                    
                    // Поддерживаем состояние "не на земле"
                    pawn.Flags &= ~((uint)PlayerFlags.FL_ONGROUND);
                }
            }
            
            // Если игрок отпустил кнопку атаки (бросил гранату)
            if (wasAttacking && !isAttacking && needGroundTeleport.ContainsKey(userId) && needGroundTeleport[userId])
            {
                // Граната уже брошена с уровня земли
                // Останавливаем симуляцию
                StopJumpSimulation(player);
            }
        }

        private void StopJumpSimulation(CCSPlayerController player)
        {
            if (player?.PlayerPawn?.Value == null) return;

            var pawn = player.PlayerPawn.Value;
            int userId = player.UserId!.Value;
            
            // Финальный телепорт на исходную позицию для точного возврата
            if (storedPosition.TryGetValue(userId, out var storedPos))
            {
                pawn.Teleport(storedPos, pawn.EyeAngles, new Vector(0, 0, 0));
                storedPosition.Remove(userId);
            }
            
            // Восстанавливаем флаг "на земле"
            pawn.Flags |= (uint)PlayerFlags.FL_ONGROUND;
            
            // Убираем из активных симуляций
            jumpSimulationActive[userId] = false;
            originalVelocity.Remove(userId);
            needGroundTeleport.Remove(userId);
            isHoldingW.Remove(userId);
        }

        private bool IsGrenadeWeapon(string designerName)
        {
            return designerName.Contains("weapon_hegrenade") ||
                   designerName.Contains("weapon_flashbang") ||
                   designerName.Contains("weapon_smokegrenade") ||
                   designerName.Contains("weapon_decoy") ||
                   designerName.Contains("weapon_molotov") ||
                   designerName.Contains("weapon_incgrenade");
        }

        private void PrintToPlayerCenter(CCSPlayerController player, string message)
        {
            if (!IsPlayerValid(player)) return;
            player.PrintToCenter(message);
        }

        // Команда для тестирования
        [ConsoleCommand("css_jt", "Jumpthrow preview info")]
        public void OnJTCommand(CCSPlayerController? player, CommandInfo? command)
        {
            if (!isPractice || !IsPlayerValid(player)) return;
            
            PrintToPlayerChat(player, $"{ChatColors.Green}Jumpthrow Preview:");
            PrintToPlayerChat(player, $"{ChatColors.Yellow}Hold grenade → Hold R for jumpthrow preview");
            PrintToPlayerChat(player, $"{ChatColors.Yellow}Hold R+W for jumpthrow with forward movement");
            PrintToPlayerChat(player, $"{ChatColors.Yellow}Click to throw with jump velocity");
        }

        // Очистка при выходе из practice mode
        public void CleanupJumpthrowPreview()
        {
            var players = Utilities.GetPlayers();
            if (players != null)
            {
                foreach (var player in players)
                {
                    if (player != null && player.UserId.HasValue)
                    {
                        int userId = player.UserId.Value;
                        if (jumpSimulationActive.ContainsKey(userId) && jumpSimulationActive[userId])
                        {
                            StopJumpSimulation(player);
                        }
                    }
                }
            }
            
            jumpSimulationActive.Clear();
            originalVelocity.Clear();
            storedPosition.Clear();
            previousButtons.Clear();
            needGroundTeleport.Clear();
            isHoldingW.Clear();
        }
    }
}