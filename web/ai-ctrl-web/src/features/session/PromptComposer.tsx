import type { KeyboardEvent } from 'react'
import { Send, Square } from 'lucide-react'
import type { ModelOption } from '../../api/types'
import { Button } from '../../components/Button'
import { modelToKey } from './modelValue'
import styles from './PromptComposer.module.css'

type PromptComposerProps = {
  text: string
  onTextChange: (value: string) => void
  models: readonly ModelOption[]
  modelsLoading: boolean
  selectedModelKey: string
  onModelChange: (key: string) => void
  disabled: boolean
  disabledReason: string | null
  mode: 'prompt' | 'abort-and-prompt'
  sending: boolean
  onSend: () => void
}

export function PromptComposer({
  text,
  onTextChange,
  models,
  modelsLoading,
  selectedModelKey,
  onModelChange,
  disabled,
  disabledReason,
  mode,
  sending,
  onSend,
}: PromptComposerProps) {
  const hasKnownSelection = models.some(
    (model) => modelToKey(model) === selectedModelKey,
  )
  const canSend = !disabled && !sending && text.trim().length > 0

  const submit = () => {
    if (canSend) {
      onSend()
    }
  }

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (
      event.key === 'Enter' &&
      !event.shiftKey &&
      !event.nativeEvent.isComposing
    ) {
      event.preventDefault()
      submit()
    }
  }

  return (
    <section className={styles.composer} aria-label="프롬프트 입력">
      <div className={styles.controls}>
        <label className={styles.modelLabel} htmlFor="model-select">
          모델
        </label>
        <select
          id="model-select"
          className={styles.modelSelect}
          value={selectedModelKey}
          onChange={(event) => onModelChange(event.target.value)}
          disabled={modelsLoading || disabled}
        >
          <option value="">세션 기본 모델</option>
          {selectedModelKey && !hasKnownSelection ? (
            <option value={selectedModelKey}>저장된 모델</option>
          ) : null}
          {models.map((model) => (
            <option key={modelToKey(model)} value={modelToKey(model)}>
              {model.name ?? model.modelId}
            </option>
          ))}
        </select>
        {modelsLoading ? <span className={styles.controlHint}>모델 불러오는 중…</span> : null}
      </div>

      <textarea
        className={styles.textarea}
        value={text}
        onChange={(event) => onTextChange(event.target.value)}
        onKeyDown={onKeyDown}
        placeholder={disabled ? '이 세션에는 프롬프트를 보낼 수 없습니다.' : '프롬프트를 입력하세요.'}
        rows={3}
        disabled={disabled}
        aria-describedby="composer-hint"
      />

      <div className={styles.footer}>
        <p id="composer-hint" className={styles.hint}>
          {disabled
            ? (disabledReason ?? '현재 상태에서는 전송할 수 없습니다.')
            : 'Enter로 전송 · Shift+Enter로 줄바꿈'}
        </p>
        <Button
          tone={mode === 'abort-and-prompt' ? 'danger' : 'primary'}
          onClick={submit}
          disabled={!canSend}
          loading={sending}
          icon={
            mode === 'abort-and-prompt' ? (
              <Square size={14} aria-hidden="true" />
            ) : (
              <Send size={14} aria-hidden="true" />
            )
          }
        >
          {mode === 'abort-and-prompt' ? '중단 후 전송' : '전송'}
        </Button>
      </div>
    </section>
  )
}
