import { useEffect, useId, useState } from 'react'
import { X } from 'lucide-react'
import { Button } from './Button'
import styles from './TextPromptDialog.module.css'

type TextPromptDialogProps = {
  open: boolean
  title: string
  description?: string
  label: string
  initialValue?: string
  placeholder?: string
  confirmLabel: string
  busy?: boolean
  onSubmit: (value: string) => void
  onClose: () => void
}

export function TextPromptDialog({ open, ...props }: TextPromptDialogProps) {
  if (!open) {
    return null
  }
  return <TextPromptDialogContent {...props} />
}

type TextPromptDialogContentProps = Omit<TextPromptDialogProps, 'open'>

function TextPromptDialogContent({
  title,
  description,
  label,
  initialValue = '',
  placeholder,
  confirmLabel,
  busy = false,
  onSubmit,
  onClose,
}: TextPromptDialogContentProps) {
  const inputId = useId()
  const titleId = useId()
  const [value, setValue] = useState(initialValue)

  useEffect(() => {
    const previous = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) {
        onClose()
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => {
      document.body.style.overflow = previous
      window.removeEventListener('keydown', onKeyDown)
    }
  }, [busy, onClose])

  const submit = () => {
    const next = value.trim()
    if (!next || busy) {
      return
    }
    onSubmit(next)
  }

  return (
    <div
      className={styles.backdrop}
      onMouseDown={(event) => {
        if (event.target === event.currentTarget && !busy) {
          onClose()
        }
      }}
    >
      <div className={styles.dialog} role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <div className={styles.header}>
          <h2 id={titleId} className={styles.title}>
            {title}
          </h2>
          <button
            type="button"
            className={styles.close}
            aria-label="닫기"
            onClick={onClose}
            disabled={busy}
          >
            <X size={17} aria-hidden="true" />
          </button>
        </div>
        {description ? <p className={styles.description}>{description}</p> : null}
        <form
          onSubmit={(event) => {
            event.preventDefault()
            submit()
          }}
        >
          <label className={styles.label} htmlFor={inputId}>
            {label}
          </label>
          <input
            id={inputId}
            className={styles.input}
            value={value}
            placeholder={placeholder}
            onChange={(event) => setValue(event.target.value)}
            autoFocus
            autoComplete="off"
            spellCheck={false}
            disabled={busy}
          />
          <div className={styles.actions}>
            <Button tone="secondary" onClick={onClose} disabled={busy}>
              취소
            </Button>
            <Button
              tone="primary"
              type="submit"
              loading={busy}
              disabled={!value.trim()}
            >
              {confirmLabel}
            </Button>
          </div>
        </form>
      </div>
    </div>
  )
}
