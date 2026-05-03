using UnityEngine;

namespace RPG.Character
{
    /// <summary>
    /// Interface implementada por qualquer entidade selecionável:
    /// monstros, NPCs, outros jogadores.
    /// </summary>
    public interface ITargetable
    {
        string  DisplayName { get; }
        float   CurrentHP   { get; }
        float   MaxHP       { get; }
        bool    IsDead      { get; }
        Vector3 Position    { get; }

        void OnSelected();
        void OnDeselected();

        /// <summary>
        /// TakeDamage via ITargetable é usado SOMENTE em modo offline/testes.
        /// Em modo multiplayer, o dano passa por CmdRequestAttack no servidor.
        /// </summary>
        void TakeDamage(float rawAtk, float rawMatk, bool isPhysical);
    }

    /// <summary>
    /// Componente base para entidades selecionáveis.
    /// Gerencia o indicador visual de seleção (círculo no chão).
    /// </summary>
    public abstract class TargetableEntity : MonoBehaviour, ITargetable
    {
        [Header("Targetable")]
        [SerializeField] protected string     displayName        = "Entity";
        [SerializeField] protected GameObject selectionIndicator;

        public virtual string  DisplayName => displayName;
        public virtual Vector3 Position    => transform.position;

        public abstract float CurrentHP { get; }
        public abstract float MaxHP     { get; }
        public abstract bool  IsDead    { get; }

        public virtual void OnSelected()
        {
            if (selectionIndicator != null)
                selectionIndicator.SetActive(true);
        }

        public virtual void OnDeselected()
        {
            if (selectionIndicator != null)
                selectionIndicator.SetActive(false);
        }

        public abstract void TakeDamage(float rawAtk, float rawMatk, bool isPhysical);

        protected virtual void Awake()
        {
            if (selectionIndicator != null)
                selectionIndicator.SetActive(false);
        }
    }
}